using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Agents;

/// <summary>An error the agent sent back, or the agent stopping mid-request.</summary>
public sealed class AcpException(string message) : Exception(message);

/// <summary>A piece of a prompt: text the user typed, or a file the agent should look at.</summary>
public abstract record AcpPromptBlock
{
    public sealed record Text(string Value) : AcpPromptBlock;

    /// <summary>A file by its path; the agent reads it itself if it needs to.</summary>
    public sealed record File(string Path) : AcpPromptBlock;
}

/// <summary>
/// A client for the Agent Client Protocol (ACP, https://agentclientprotocol.com): the editor side of the
/// conversation with a coding agent such as claude-agent-acp, as LspClient is for a language server.
///
/// The agent is a child process speaking JSON-RPC 2.0 over stdio, one JSON message per line. The IDE starts a
/// session in the workspace folder (<see cref="NewSessionAsync"/>), sends prompts (<see cref="PromptAsync"/>), and
/// while a prompt runs the agent streams what it does as <see cref="Updated"/> events: text, tool calls, its plan.
/// When it wants to run a tool that needs the user's say-so it asks (<see cref="PermissionHandler"/>).
///
/// The agent reads and writes files and runs commands itself, so the IDE offers no file system or terminal
/// methods (clientCapabilities are all false); the open tabs pick up its edits from disk (FileChangeWatcher).
///
/// Split across files: this one (start-up, the methods the IDE calls, Dispose) and AcpClient.Transport.cs (the
/// JSON-RPC plumbing and what the agent sends).
/// </summary>
public sealed partial class AcpClient : IDisposable
{
    /// <summary>The protocol version this client speaks (ACP v1).</summary>
    public const int ProtocolVersion = 1;

    private readonly Process? _process;

    /// <summary>The agent's name and version from initialize ("Claude Agent 0.107.2").</summary>
    public string AgentTitle { get; private set; } = "agent";

    /// <summary>
    /// Whether the agent is signed in, from claude-agent-acp's auth/status_update ("authenticated",
    /// "unauthenticated"...); null until it says. Other agents may never send it.
    /// </summary>
    public string? AuthState { get; private set; }

    /// <summary>A session/update arrived: (session id, update). Raised on a background thread.</summary>
    public event Action<string, AcpUpdate>? Updated;

    /// <summary><see cref="AuthState"/> changed. Background thread.</summary>
    public event Action<string>? AuthStateChanged;

    /// <summary>A line the agent wrote to stderr (its log). Background thread.</summary>
    public event Action<string>? AgentLog;

    /// <summary>The agent process exited (crashed or was killed). Background thread.</summary>
    public event Action? Exited;

    /// <summary>
    /// Answers the agent's permission requests: return the chosen option's id, or null for "cancelled". Called on a
    /// background thread; may take as long as the user does. With no handler every request is cancelled.
    /// </summary>
    public Func<AcpPermissionRequest, Task<string?>>? PermissionHandler { get; set; }

    /// <summary>Start the agent and do the initialize handshake.</summary>
    /// <param name="workingDirectory">The agent process's current folder (the workspace).</param>
    /// <param name="environment">Extra environment variables for the agent.</param>
    public static async Task<AcpClient> StartAsync(
        string command,
        IEnumerable<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            startInfo.Environment[name] = value;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Couldn't start '{command}'.");
        var client = new AcpClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, process);
        try
        {
            await client.InitializeAsync(ct);
            return client;
        }
        catch (Exception ex)
        {
            var log = client.RecentLog;
            client.Dispose();
            throw log.Length > 0 ? new AcpException($"{ex.Message} [{log}]") : ex;
        }
    }

    /// <summary>A client over streams that are already connected to an agent (tests use pipes); call <see cref="InitializeAsync"/> next.</summary>
    internal AcpClient(Stream fromAgent, Stream toAgent, Process? process = null)
    {
        _fromAgent = fromAgent;
        _toAgent = toAgent;
        _process = process;

        if (process is not null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { if (!_disposed) Exited?.Invoke(); };
            // Drain stderr so a chatty agent can never fill the pipe and hang.
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
            process.BeginErrorReadLine();
        }

        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>The agent can list its past sessions (session/list), for the history button.</summary>
    public bool CanListSessions { get; private set; }

    /// <summary>The agent can reopen a past session and replay it (session/load).</summary>
    public bool CanLoadSessions { get; private set; }

    /// <summary>The initialize handshake: say which protocol version and features the IDE has.</summary>
    internal async Task InitializeAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            clientCapabilities = new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false
            },
            clientInfo = new { name = "AXamlIDE", version = typeof(AcpClient).Assembly.GetName().Version?.ToString() ?? "0" }
        }, ct);

        if (result.TryGetProperty("protocolVersion", out var version) && version.TryGetInt32(out var number) && number != ProtocolVersion)
            throw new AcpException($"The agent speaks ACP version {number}; AXamlIDE speaks version {ProtocolVersion}.");

        if (result.TryGetProperty("agentCapabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
        {
            CanLoadSessions = capabilities.TryGetProperty("loadSession", out var load) && load.ValueKind == JsonValueKind.True;
            CanListSessions = capabilities.TryGetProperty("sessionCapabilities", out var sessions) && sessions.ValueKind == JsonValueKind.Object
                              && sessions.TryGetProperty("list", out _);
        }

        if (result.TryGetProperty("agentInfo", out var info))
        {
            var title = AcpParser.String(info, "title") ?? AcpParser.String(info, "name");
            var agentVersion = AcpParser.String(info, "version");
            if (title is not null)
                AgentTitle = agentVersion is null ? title : $"{title} {agentVersion}";
        }
    }

    /// <summary>Start a conversation whose tools work in <paramref name="folder"/> (the workspace root).</summary>
    public async Task<AcpSession> NewSessionAsync(string folder, CancellationToken ct = default)
    {
        var result = await RequestAsync("session/new", new { cwd = folder, mcpServers = Array.Empty<object>() }, ct);
        var id = AcpParser.String(result, "sessionId") ?? throw new AcpException("The agent didn't return a session id.");
        return new AcpSession(id, AcpParser.ConfigOptions(result));
    }

    /// <summary>The past sessions in <paramref name="folder"/>, newest first as the agent gives them (at most <paramref name="limit"/>).</summary>
    public async Task<IReadOnlyList<AcpSessionInfo>> ListSessionsAsync(string folder, int limit = 50, CancellationToken ct = default)
    {
        var sessions = new List<AcpSessionInfo>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync("session/list", cursor is null ? new { cwd = folder } : new { cwd = folder, cursor }, ct);
            (var page, cursor) = AcpParser.SessionList(result);
            sessions.AddRange(page);
        } while (cursor is not null && sessions.Count < limit);
        return sessions.Count > limit ? sessions[..limit] : sessions;
    }

    /// <summary>
    /// Reopen a past session so the conversation can go on. Before this returns, the agent replays the conversation
    /// as <see cref="Updated"/> events for that session id (the user's messages as AcpChunkKind.User chunks).
    /// </summary>
    public async Task<AcpSession> LoadSessionAsync(string sessionId, string folder, CancellationToken ct = default)
    {
        var result = await RequestAsync("session/load", new { sessionId, cwd = folder, mcpServers = Array.Empty<object>() }, ct);
        return new AcpSession(sessionId, AcpParser.ConfigOptions(result));
    }

    /// <summary>
    /// Send the user's message and wait for the agent to finish its turn; meanwhile it reports progress as
    /// <see cref="Updated"/> events. Returns why it stopped: end_turn, cancelled (after <see cref="Cancel"/>),
    /// max_tokens, max_turn_requests or refusal.
    /// </summary>
    public async Task<string> PromptAsync(string sessionId, IReadOnlyList<AcpPromptBlock> prompt)
    {
        var blocks = prompt.Select(block => block switch
        {
            AcpPromptBlock.File file => (object)new
            {
                type = "resource_link",
                uri = new Uri(file.Path).AbsoluteUri,
                name = Path.GetFileName(file.Path)
            },
            AcpPromptBlock.Text text => new { type = "text", text = text.Value },
            _ => throw new ArgumentOutOfRangeException(nameof(prompt))
        }).ToArray();

        var result = await RequestAsync("session/prompt", new { sessionId, prompt = blocks });
        return AcpParser.String(result, "stopReason") ?? "end_turn";
    }

    /// <summary>
    /// Ask the agent to stop the running turn. It answers <see cref="PromptAsync"/> with "cancelled" once it has.
    /// Permission requests still waiting must be answered with null (the caller does that: it shows them).
    /// </summary>
    public void Cancel(string sessionId) => Notify("session/cancel", new { sessionId });

    /// <summary>Change one of the session's settings (the mode, the model); returns all of them as they now are.</summary>
    public async Task<IReadOnlyList<AcpConfigOption>> SetConfigOptionAsync(string sessionId, string configId, string value)
    {
        var result = await RequestAsync("session/set_config_option", new { sessionId, configId, value });
        return AcpParser.ConfigOptions(result);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _outbox.Writer.TryComplete();

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);   // the adapter runs claude as its own child
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
        }
        else
        {
            _toAgent.Dispose();
            _fromAgent.Dispose();
        }
        _cts.Dispose();
    }
}
