using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace AXaxmlIDE.Lsp;

/// <summary>
/// Owns one LspClient per configured server. Servers start lazily the first time a document
/// of one of their languages is attached, once per workspace. Everything here runs on the
/// UI thread except the DiagnosticsPublished forwarding, which comes from the clients'
/// reader threads (the same as LspClient's own event).
/// </summary>
public sealed class LspManager : IDisposable
{
    private sealed class ServerState(LspServerConfig config)
    {
        public LspServerConfig Config { get; } = config;
        public Task<LspClient?>? Start;
        public LspClient? Client;
        public string Status = "idle";
        public string? Progress;
        public bool ProjectsLoaded;
    }

    private readonly Dictionary<string, ServerState> _servers;
    private readonly Dictionary<string, ServerState> _byLanguage = new();
    private LspWorkspace? _workspace;

    /// <summary>Bumped whenever servers are replaced; lets async callers notice they've gone stale.</summary>
    public int Generation { get; private set; }

    /// <summary>Raised on the UI thread when any server's status text changes.</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// A server wants its open documents' diagnostics re-pulled (its projects finished loading, or it
    /// asked for a refresh). Raised on the UI thread.
    /// </summary>
    public event Action<LspClient>? DiagnosticsRefreshNeeded;

    /// <summary>(uri, diagnostics, version) from whichever server published them. Background thread.</summary>
    public event Action<string, IReadOnlyList<LspDiagnostic>, int?>? DiagnosticsPublished;
    public Func<JsonElement, Task<bool>>? WorkspaceEditHandler { get; set; }

    public LspManager(IEnumerable<LspServerConfig> configs)
    {
        _servers = configs.ToDictionary(c => c.Id, c => new ServerState(c));

        foreach (var state in _servers.Values)
            foreach (var language in state.Config.LanguageIds)
                _byLanguage[language] = state;
    }

    public bool Handles(string languageId) => _byLanguage.ContainsKey(languageId);

    /// <summary>"axsg-lsp: running  ·  csharp-ls: starting…" - only servers that have been touched.</summary>
    public string StatusSummary => string.Join("  \u00b7  ",
        _servers.Values.Where(s => s.Status != "idle").Select(s =>
            s.Progress is null ? $"{s.Config.Id}: {s.Status}" : $"{s.Config.Id}: {s.Status} ({s.Progress})"));

    /// <summary>New workspace: drop every server. Callers should then re-attach their open documents.</summary>
    public void SetWorkspace(LspWorkspace workspace)
    {
        _workspace = workspace;
        Restart();
    }

    public void ClearWorkspace()
    {
        _workspace = null;
        Restart();
    }

    /// <summary>Drop every server (e.g. one crashed). Callers should then re-attach their open documents.</summary>
    public void Restart()
    {
        Generation++;

        foreach (var state in _servers.Values)
        {
            state.Client?.Dispose();
            state.Client = null;
            state.Start = null;
            state.Status = "idle";
            state.Progress = null;
            state.ProjectsLoaded = false;
        }

        StatusChanged?.Invoke();
    }

    /// <summary>
    /// The running client for this language, starting its server if needed. Null if there is no
    /// server for the language, no workspace yet, or the server failed to start (the reason is
    /// in the status text).
    /// </summary>
    public Task<LspClient?> GetClientAsync(string languageId)
    {
        if (_workspace is null || !_byLanguage.TryGetValue(languageId, out var state))
            return Task.FromResult<LspClient?>(null);

        return state.Start ??= StartAsync(state, _workspace, Generation);
    }

    private async Task<LspClient?> StartAsync(ServerState state, LspWorkspace workspace, int generation)
    {
        var config = state.Config;

        var command = config.ResolveCommand();
        if (command is null)
        {
            SetStatus(state, $"not found - {config.InstallHint}");
            return null;
        }

        SetStatus(state, "starting\u2026");
        state.ProjectsLoaded = false;
        state.Progress = null;

        try
        {
            var args = config.BuildArgs?.Invoke(workspace) ?? [];
            var client = await LspClient.StartAsync(command, args, workspace.RootPath);

            if (generation != Generation)   // workspace changed while we were starting
            {
                client.Dispose();
                return null;
            }

            client.DiagnosticsPublished += (uri, list, version) => DiagnosticsPublished?.Invoke(uri, list, version);
            client.WorkspaceEditHandler = edit => WorkspaceEditHandler?.Invoke(edit)
                ?? Task.FromResult(false);
            client.ServerLog += message => Debug.WriteLine($"[{config.Id}] {message}");
            client.Exited += () => Dispatcher.UIThread.Post(() =>
            {
                if (generation != Generation) return;

                state.Client = null;
                state.Start = null;   // the next document of this language starts a fresh server
                SetStatus(state, "stopped");
            });

            // These arrive on the client's reader thread; state is only touched on the UI thread.
            client.Progress += text => Dispatcher.UIThread.Post(() =>
            {
                if (generation != Generation) return;
                state.Progress = text;
                StatusChanged?.Invoke();
            });
            client.DiagnosticRefreshRequested += () => Dispatcher.UIThread.Post(() =>
            {
                if (generation == Generation) DiagnosticsRefreshNeeded?.Invoke(client);
            });
            client.ProjectInitializationComplete += () => Dispatcher.UIThread.Post(() =>
            {
                if (generation != Generation) return;
                state.ProjectsLoaded = true;
                SetStatus(state, "running");
                DiagnosticsRefreshNeeded?.Invoke(client);   // answers were empty until now
            });

            if (config.AfterInitialize is { } afterInitialize)
                await afterInitialize(client, workspace);

            if (generation != Generation)
            {
                client.Dispose();
                return null;
            }

            state.Client = client;
            SetStatus(state, config.WaitsForProjectInitialization && !state.ProjectsLoaded
                ? "loading solution\u2026"
                : "running");
            return client;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{config.Id}] failed to start: {ex}");
            if (generation == Generation) SetStatus(state, "failed - " + FirstLine(ex.Message));
            return null;
        }
    }

    // The status bar is one line; the full text went to the debug output.
    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "\u2026" : line;
    }

    private void SetStatus(ServerState state, string status)
    {
        state.Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        foreach (var state in _servers.Values)
            state.Client?.Dispose();
    }
}