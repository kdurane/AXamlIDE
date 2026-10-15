using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AXaxmlIDE.Interfaces;
// ReSharper disable SuggestVarOrType_SimpleTypes

namespace AXaxmlIDE.Lsp;

public sealed class LspException(string message) : Exception(message);

/// <summary>
/// A small, dependency-free LSP client: JSON-RPC 2.0 over stdio with Content-Length
/// framing. One long-lived server process, one reader loop, one writer loop (a
/// channel, so notifications always go out in the order they were queued -
/// didChange ordering matters).
/// </summary>
public sealed class LspClient : ILspSender, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // A JsonElement is a struct, so it is written as `null` rather than being
    // skipped by WhenWritingNull - needed for "result": null responses.
    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement;
    private static readonly JsonElement JsonEmptyArray = JsonDocument.Parse("[]").RootElement;

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Channel<byte[]> _outbox =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private volatile bool _disposed;

    public bool SupportsIncrementalSync { get; private set; }
    public IReadOnlyList<string> CompletionTriggerCharacters { get; private set; } = [];
    public IReadOnlyList<string> SignatureHelpTriggerCharacters { get; private set; } = [];
    public IReadOnlyList<string> SignatureHelpRetriggerCharacters { get; private set; } = [];
    public bool SupportsDefinition { get; private set; }
    public bool SupportsReferences { get; private set; }
    public bool SupportsRename { get; private set; }
    public bool SupportsWorkspaceSymbols { get; private set; }
    public bool SupportsDocumentSymbols { get; private set; }
    public bool SupportsCodeActions { get; private set; }
    public bool SupportsCodeActionResolve { get; private set; }
    public bool SupportsDocumentFormatting { get; private set; }

    /// <summary>True if the server wants diagnostics pulled (textDocument/diagnostic) rather than pushing them.</summary>
    public bool SupportsPullDiagnostics { get; private set; }

    // Pull-diagnostic identifiers: the one in the static capability plus any registered dynamically.
    // Locked on itself; usually empty or one entry.
    private readonly List<string> _diagnosticIdentifiers = [];
    private readonly HashSet<string> _dynamicCapabilities = new(StringComparer.Ordinal);
    private bool _staticDefinition;
    private bool _staticReferences;
    private bool _staticRename;
    private bool _staticWorkspaceSymbols;
    private bool _staticDocumentSymbols;
    private bool _staticCodeActions;
    private bool _staticCodeActionResolve;
    private bool _dynamicCodeActionResolve;
    private bool _staticDocumentFormatting;

    // Work-done progress per token, for the status bar. Locked on itself.
    private readonly Dictionary<string, (string Title, string? Message)> _progress = new();

    // The last few server log lines, so a server that dies during startup can say why.
    private readonly Queue<string> _recentLog = new();
    public string RecentLog { get { lock (_recentLog) return string.Join(" | ", _recentLog); } }

    /// <summary>Raised on a background thread: (uri, diagnostics, version if the server sent one).</summary>
    public event Action<string, IReadOnlyList<LspDiagnostic>, int?>? DiagnosticsPublished;
    public event Action<string>? ServerLog;
    public event Action? Exited;

    /// <summary>The server finished loading its projects (Roslyn's workspace/projectInitializationComplete). Background thread.</summary>
    public event Action? ProjectInitializationComplete;

    /// <summary>The server wants open documents' diagnostics re-pulled. Background thread.</summary>
    public event Action? DiagnosticRefreshRequested;
    public Func<JsonElement, Task<bool>>? WorkspaceEditHandler { get; set; }

    /// <summary>Current work-done progress text ("Loading projects: ..."), or null when idle. Background thread.</summary>
    public event Action<string?>? Progress;

    private LspClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = new BufferedStream(process.StandardOutput.BaseStream);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => { if (!_disposed) Exited?.Invoke(); };

        // Drain stderr so a chatty server can never fill the pipe and hang.
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.BeginErrorReadLine();

        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    private void Log(string line)
    {
        lock (_recentLog)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > 8) _recentLog.Dequeue();
        }

        ServerLog?.Invoke(line);
    }

    public static async Task<LspClient> StartAsync(
        string command, IEnumerable<string> args, string rootPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = rootPath
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start '{command}'.");

        var client = new LspClient(process);
        try
        {
            await client.InitializeAsync(rootPath, ct);
            return client;
        }
        catch (Exception ex)
        {
            var log = client.RecentLog;
            client.Dispose();

            if (log.Length > 0)
                throw new InvalidOperationException($"{ex.Message} [{log}]", ex);
            throw;
        }
    }

    // ---------------------------------------------------------------- handshake

    private string[] _hoverContext = ["markdown", "plaintext"];

    private async Task InitializeAsync(string rootPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var rootUri = new Uri(rootPath).AbsoluteUri;

        var result = await RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "AXaxmlIDE", version = "0.1" },
            rootUri,
            workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(rootPath) } },
            capabilities = new
            {
                workspace = new
                {
                    workspaceFolders = true,
                    configuration = true,
                    diagnostics = new { refreshSupport = true },
                    applyEdit = true,
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        failureHandling = "abort"
                    }
                },
                window = new { workDoneProgress = true },
                textDocument = new
                {
                    synchronization = new { dynamicRegistration = false, didSave = true },
                    publishDiagnostics = new { versionSupport = true },
                    diagnostic = new { dynamicRegistration = true, relatedDocumentSupport = false },
                    codeAction = new
                    {
                        dynamicRegistration = true,
                        codeActionLiteralSupport = new
                        {
                            codeActionKind = new
                            {
                                valueSet = new[]
                                {
                                    "", "quickfix", "refactor", "refactor.extract", "refactor.inline",
                                    "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"
                                }
                            }
                        },
                        resolveSupport = new { properties = new[] { "edit", "command" } }
                    },
                    hover = new { contentFormat = _hoverContext },
                    completion = new { completionItem = new { snippetSupport = false } },
                    signatureHelp = new
                    {
                        dynamicRegistration = false,
                        contextSupport = true,
                        signatureInformation = new
                        {
                            documentationFormat = new[] { "markdown", "plaintext" },
                            activeParameterSupport = true,
                            parameterInformation = new { labelOffsetSupport = true }
                        }
                    }
                }
            }
        }, timeout.Token);

        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("capabilities", out var caps))
        {
            if (caps.TryGetProperty("textDocumentSync", out var sync))
            {
                // Either a bare number, or { openClose, change, save }.
                var kind = sync.ValueKind switch
                {
                    JsonValueKind.Number => sync.GetInt32(),
                    JsonValueKind.Object when sync.TryGetProperty("change", out var c)
                                              && c.ValueKind == JsonValueKind.Number => c.GetInt32(),
                    _ => 1
                };
                SupportsIncrementalSync = kind == 2;
            }

            if (caps.TryGetProperty("completionProvider", out var comp)
                && comp.TryGetProperty("triggerCharacters", out var trig)
                && trig.ValueKind == JsonValueKind.Array)
            {
                CompletionTriggerCharacters = trig.EnumerateArray()
                    .Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToArray();
            }

            if (caps.TryGetProperty("signatureHelpProvider", out var signatureHelp)
                && signatureHelp.ValueKind == JsonValueKind.Object)
            {
                SignatureHelpTriggerCharacters = ReadStringArray(signatureHelp, "triggerCharacters");
                SignatureHelpRetriggerCharacters = ReadStringArray(signatureHelp, "retriggerCharacters");
            }

            _staticDefinition = HasProvider(caps, "definitionProvider");
            _staticReferences = HasProvider(caps, "referencesProvider");
            _staticRename = HasProvider(caps, "renameProvider");
            _staticWorkspaceSymbols = HasProvider(caps, "workspaceSymbolProvider");
            _staticDocumentSymbols = HasProvider(caps, "documentSymbolProvider");
            _staticCodeActions = HasProvider(caps, "codeActionProvider");
            _staticCodeActionResolve = caps.TryGetProperty("codeActionProvider", out var codeActionProvider)
                                       && codeActionProvider.ValueKind == JsonValueKind.Object
                                       && codeActionProvider.TryGetProperty("resolveProvider", out var resolveProvider)
                                       && resolveProvider.ValueKind == JsonValueKind.True;
            _staticDocumentFormatting = HasProvider(caps, "documentFormattingProvider");
            SupportsDefinition = _staticDefinition || _dynamicCapabilities.Contains("textDocument/definition");
            SupportsReferences = _staticReferences || _dynamicCapabilities.Contains("textDocument/references");
            SupportsRename = _staticRename || _dynamicCapabilities.Contains("textDocument/rename");
            SupportsWorkspaceSymbols = _staticWorkspaceSymbols || _dynamicCapabilities.Contains("workspace/symbol");
            SupportsDocumentSymbols = _staticDocumentSymbols || _dynamicCapabilities.Contains("textDocument/documentSymbol");
            SupportsCodeActions = _staticCodeActions || _dynamicCapabilities.Contains("textDocument/codeAction");
            SupportsCodeActionResolve = _staticCodeActionResolve || _dynamicCodeActionResolve;
            SupportsDocumentFormatting = _staticDocumentFormatting
                                         || _dynamicCapabilities.Contains("textDocument/formatting");

            // DiagnosticOptions: its mere presence means the server supports pulling.
            if (caps.TryGetProperty("diagnosticProvider", out var diag)
                && diag.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            {
                SupportsPullDiagnostics = true;
                if (diag.ValueKind == JsonValueKind.Object)
                    AddDiagnosticIdentifier(Str(diag, "identifier"));
            }
        }

        Notify("initialized", new { });
    }

    // ---------------------------------------------------------- notifications

    public void DidOpen(string uri, string languageId, int version, string text)
        => Notify("textDocument/didOpen", new { textDocument = new { uri, languageId, version, text } });

    public void DidChange(string uri, int version, IReadOnlyList<ContentChange> changes)
        => Notify("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = changes.Select(c => new
            {
                range = new
                {
                    start = new { line = c.StartLine, character = c.StartChar },
                    end = new { line = c.EndLine, character = c.EndChar }
                },
                text = c.Text
            }).ToArray()
        });

    public void DidChangeFull(string uri, int version, string text)
        => Notify("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } }
        });

    public void DidSave(string uri, string? text = null)
        => Notify("textDocument/didSave", new { textDocument = new { uri }, text });

    public void DidClose(string uri)
        => Notify("textDocument/didClose", new { textDocument = new { uri } });

    // ------------------------------------------------------------- plumbing

    /// <summary>Send any notification, e.g. a server's custom one from an AfterInitialize hook.</summary>
    public void Notify(string method, object? @params)
        => Enqueue(new { jsonrpc = "2.0", method, @params });

    private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            Notify("$/cancelRequest", new { id });
        });
        Enqueue(new { jsonrpc = "2.0", id, method, @params });

        try { return await tcs.Task; }
        finally { _pending.TryRemove(id, out _); }
    }

    private void Enqueue(object message)
    {
        if (_disposed) return;

        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");

        var frame = new byte[header.Length + json.Length];
        header.CopyTo(frame, 0);
        json.CopyTo(frame, header.Length);

        _outbox.Writer.TryWrite(frame);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _stdin.WriteAsync(frame, _cts.Token);
                await _stdin.FlushAsync(_cts.Token);
            }
        }
        catch (Exception) { /* server gone or disposing */ }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var contentLength = -1;
                while (true)
                {
                    var line = await ReadHeaderLineAsync();
                    if (line is null) return;               // EOF: server exited
                    if (line.Length == 0) break;            // blank line ends the headers
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                }
                if (contentLength < 0) continue;

                var body = new byte[contentLength];
                await _stdout.ReadExactlyAsync(body, _cts.Token);

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    Dispatch(doc.RootElement);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    ServerLog?.Invoke("Bad message from server: " + ex.Message);
                }
            }
        }
        catch (Exception) { /* EOF / cancelled */ }
        finally
        {
            foreach (var p in _pending.Values)
                p.TrySetException(new IOException("The language server exited."));
        }
    }

    private async Task<string?> ReadHeaderLineAsync()
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await _stdout.ReadAsync(one, _cts.Token) == 0) return null;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------- dispatch

    private void Dispatch(JsonElement root)
    {
        var hasId = root.TryGetProperty("id", out var id);
        var hasMethod = root.TryGetProperty("method", out var method);

        if (hasMethod && hasId) _ = HandleServerRequestAsync(id.Clone(), method.GetString()!, root);
        else if (hasMethod) HandleNotification(method.GetString()!, root);
        else if (hasId) HandleResponse(id, root);
    }

    private void HandleResponse(JsonElement idEl, JsonElement root)
    {
        if (!idEl.TryGetInt32(out var id) || !_pending.TryRemove(id, out var tcs)) return;

        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
            tcs.TrySetException(new LspException(msg ?? "unknown error"));
        }
        else
        {
            // Clone: the JsonDocument is disposed as soon as Dispatch returns.
            tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : JsonNull);
        }
    }

    // Servers call back too (workspace/configuration, client/registerCapability,
    // window/workDoneProgress/create ...). Every request needs *a* response.
    private async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement root)
    {
        object result = JsonNull;
        root.TryGetProperty("params", out var p);   // Undefined when the request carries none

        switch (method)
        {
            case "workspace/configuration" when p.ValueKind == JsonValueKind.Object
                                                && p.TryGetProperty("items", out var items)
                                                && items.ValueKind == JsonValueKind.Array:
                result = items.EnumerateArray().Select(_ => JsonNull).ToArray();   // one null per item
                break;

            case "client/registerCapability":
                RegisterCapabilities(p);
                break;

            case "client/unregisterCapability":
                UnregisterCapabilities(p);
                break;

            case "workspace/diagnostic/refresh":
                DiagnosticRefreshRequested?.Invoke();
                break;

            case "workspace/applyEdit":
                var applied = false;
                if (WorkspaceEditHandler is { } handler
                    && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("edit", out var edit))
                {
                    try
                    {
                        applied = await handler(edit.Clone());
                    }
                    catch (Exception ex)
                    {
                        Log($"Couldn't apply language-server workspace edit: {ex.Message}");
                    }
                }
                result = new { applied };
                break;
        }

        Enqueue(new { jsonrpc = "2.0", id, result });
    }

    private void RegisterCapabilities(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("registrations", out var registrations)
            || registrations.ValueKind != JsonValueKind.Array)
            return;

        var anyDiagnostics = false;

        foreach (var registration in registrations.EnumerateArray())
        {
            var method = Str(registration, "method");
            if (method is "textDocument/definition" or "textDocument/references"
                or "textDocument/rename" or "workspace/symbol" or "textDocument/codeAction"
                or "textDocument/formatting" or "textDocument/documentSymbol")
            {
                _dynamicCapabilities.Add(method);
                SupportsDefinition |= method == "textDocument/definition";
                SupportsReferences |= method == "textDocument/references";
                SupportsRename |= method == "textDocument/rename";
                SupportsWorkspaceSymbols |= method == "workspace/symbol";
                SupportsDocumentSymbols |= method == "textDocument/documentSymbol";
                SupportsCodeActions |= method == "textDocument/codeAction";
                SupportsDocumentFormatting |= method == "textDocument/formatting";
                if (method == "textDocument/codeAction"
                    && registration.TryGetProperty("registerOptions", out var actionOptions)
                    && actionOptions.ValueKind == JsonValueKind.Object)
                    _dynamicCodeActionResolve = actionOptions.TryGetProperty("resolveProvider", out var resolveProvider)
                                                && resolveProvider.ValueKind == JsonValueKind.True;
            }

            if (method != "textDocument/diagnostic")
                continue;

            anyDiagnostics = true;
            SupportsPullDiagnostics = true;

            if (registration.TryGetProperty("registerOptions", out var options)
                && options.ValueKind == JsonValueKind.Object)
                AddDiagnosticIdentifier(Str(options, "identifier"));
        }

        // Documents attached before this arrived have not been pulled yet.
        if (anyDiagnostics) DiagnosticRefreshRequested?.Invoke();
    }

    private void UnregisterCapabilities(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object
            || (!p.TryGetProperty("unregisterations", out var registrations)
                && !p.TryGetProperty("unregistrations", out registrations))
            || registrations.ValueKind != JsonValueKind.Array)
            return;

        var removedCodeActions = false;
        foreach (var registration in registrations.EnumerateArray())
            if (Str(registration, "method") is { } method)
            {
                _dynamicCapabilities.Remove(method);
                removedCodeActions |= method == "textDocument/codeAction";
            }

        if (removedCodeActions)
            _dynamicCodeActionResolve = false;

        SupportsDefinition = _staticDefinition || _dynamicCapabilities.Contains("textDocument/definition");
        SupportsReferences = _staticReferences || _dynamicCapabilities.Contains("textDocument/references");
        SupportsRename = _staticRename || _dynamicCapabilities.Contains("textDocument/rename");
        SupportsWorkspaceSymbols = _staticWorkspaceSymbols || _dynamicCapabilities.Contains("workspace/symbol");
        SupportsDocumentSymbols = _staticDocumentSymbols || _dynamicCapabilities.Contains("textDocument/documentSymbol");
        SupportsCodeActions = _staticCodeActions || _dynamicCapabilities.Contains("textDocument/codeAction");
        SupportsCodeActionResolve = _staticCodeActionResolve || _dynamicCodeActionResolve;
        SupportsDocumentFormatting = _staticDocumentFormatting
                                     || _dynamicCapabilities.Contains("textDocument/formatting");
    }

    private void AddDiagnosticIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return;

        lock (_diagnosticIdentifiers)
            if (!_diagnosticIdentifiers.Contains(identifier))
                _diagnosticIdentifiers.Add(identifier);
    }

    private void HandleNotification(string method, JsonElement root)
    {
        root.TryGetProperty("params", out var p);   // some notifications carry none

        switch (method)
        {
            case "workspace/projectInitializationComplete":
                ProjectInitializationComplete?.Invoke();
                break;

            case "$/progress" when p.ValueKind == JsonValueKind.Object:
                HandleProgress(p);
                break;

            case "textDocument/publishDiagnostics" when p.ValueKind == JsonValueKind.Object:
                HandlePublishDiagnostics(p);
                break;

            case "window/logMessage" or "window/showMessage"
                when p.ValueKind == JsonValueKind.Object && Str(p, "message") is { } message:
                Log(message);
                break;
        }
    }

    // $/progress for server-initiated work (window/workDoneProgress/create), e.g. loading projects.
    private void HandleProgress(JsonElement p)
    {
        if (!p.TryGetProperty("token", out var tokenElement)
            || !p.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object)
            return;

        var token = tokenElement.GetRawText();   // the token may be a string or a number
        string? text;

        lock (_progress)
        {
            switch (Str(value, "kind"))
            {
                case "begin":
                    _progress[token] = (Str(value, "title") ?? "", Str(value, "message"));
                    break;
                case "report" when _progress.TryGetValue(token, out var current):
                    _progress[token] = (current.Title, Str(value, "message") ?? current.Message);
                    break;
                case "end":
                    _progress.Remove(token);
                    break;
                default:
                    return;
            }

            text = _progress.Count == 0
                ? null
                : string.Join("; ", _progress.Values.Select(v =>
                    string.IsNullOrEmpty(v.Message) ? v.Title : $"{v.Title}: {v.Message}"));
        }

        Progress?.Invoke(text);
    }

    private void HandlePublishDiagnostics(JsonElement p)
    {
        var uri = p.GetProperty("uri").GetString()!;

        int? version = p.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

        var list = p.GetProperty("diagnostics").EnumerateArray().Select(ParseDiagnostic).ToList();

        DiagnosticsPublished?.Invoke(uri, list, version);
    }

    private static LspDiagnostic ParseDiagnostic(JsonElement d)
    {
        var range = ParseRange(d.GetProperty("range"));

        var severity = d.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.Number
            ? sev.GetInt32()
            : 1;

        var message = ReadMarkup(d.GetProperty("message"));

        // Lead with the code (CS0103, IDE0005 ...) when there is one.
        if (!d.TryGetProperty("code", out var code))
            return new LspDiagnostic(range.StartLine, range.StartChar, range.EndLine, range.EndChar, severity, message, d.Clone());
        var codeText = code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.GetRawText(),
            _ => null
        };
        if (!string.IsNullOrEmpty(codeText)) message = $"{codeText}: {message}";

        return new LspDiagnostic(range.StartLine, range.StartChar, range.EndLine, range.EndChar, severity, message, d.Clone());
    }

    /// <summary>
    /// Pull the document's diagnostics (textDocument/diagnostic). One request per registered
    /// identifier, merged. Always asks for a full report: we never send a previousResultId.
    /// </summary>
    public async Task<IReadOnlyList<LspDiagnostic>> PullDiagnosticsAsync(string uri, CancellationToken ct = default)
    {
        string?[] identifiers;
        lock (_diagnosticIdentifiers)
            identifiers = _diagnosticIdentifiers.Count > 0 ? [.. _diagnosticIdentifiers] : [null];

        var reports = await Task.WhenAll(identifiers.Select(id => PullOneAsync(uri, id, ct)));
        return [.. reports.SelectMany(r => r)];
    }

    private async Task<IReadOnlyList<LspDiagnostic>> PullOneAsync(string uri, string? identifier, CancellationToken ct)
    {
        var result = await RequestAsync("textDocument/diagnostic", new
        {
            textDocument = new { uri },
            identifier
        }, ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        return [.. items.EnumerateArray().Select(ParseDiagnostic)];
    }

    // ------------------------------------------------------- hover / completion

    public async Task<LspHover?> HoverAsync(string uri, int line, int character, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line, character }
        }, ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("contents", out var contents))
            return null;

        var (content, isMarkdown) = ReadHoverMarkup(contents);
        content = content.Trim();

        if (content.Length == 0)
            return null;

        LspRange? range = result.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Object
            ? ParseRange(r)
            : null;

        return new LspHover(content, range, isMarkdown);
    }

    public async Task<IReadOnlyList<LspCompletionItem>> CompletionAsync(
        string uri, int line, int character, string? triggerCharacter, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/completion", new
        {
            textDocument = new { uri },
            position = new { line, character },
            // 1 = invoked, 2 = trigger character
            context = new
            {
                triggerKind = triggerCharacter is null ? 1 : 2,
                triggerCharacter
            }
        }, ct);

        // Either CompletionItem[] or { isIncomplete, items }.
        JsonElement items = result.ValueKind switch
        {
            JsonValueKind.Array => result,
            JsonValueKind.Object when result.TryGetProperty("items", out var it) => it,
            _ => default
        };

        if (items.ValueKind != JsonValueKind.Array) return [];

        return [.. items.EnumerateArray().Select(ParseCompletionItem)];
    }

    public async Task<LspSignatureHelp?> SignatureHelpAsync(
        string uri,
        int line,
        int character,
        string? triggerCharacter,
        bool isRetrigger = false,
        CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/signatureHelp", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new
            {
                triggerKind = isRetrigger ? 3 : triggerCharacter is null ? 1 : 2,
                triggerCharacter,
                isRetrigger
            }
        }, ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("signatures", out var signatures)
            || signatures.ValueKind != JsonValueKind.Array)
            return null;

        var items = signatures.EnumerateArray().Select(ParseSignatureInformation).ToArray();
        if (items.Length == 0)
            return null;

        var activeSignature = result.TryGetProperty("activeSignature", out var signatureIndex)
                              && signatureIndex.ValueKind == JsonValueKind.Number
            ? Math.Clamp(signatureIndex.GetInt32(), 0, items.Length - 1)
            : 0;
        int? activeParameter = result.TryGetProperty("activeParameter", out var parameterIndex)
                               && parameterIndex.ValueKind == JsonValueKind.Number
            ? parameterIndex.GetInt32()
            : null;

        return new LspSignatureHelp(items, activeSignature, activeParameter);
    }

    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(
        string uri, int line, int character, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/definition", new
        {
            textDocument = new { uri },
            position = new { line, character }
        }, ct);
        return ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri, int line, int character, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/references", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new { includeDeclaration = true }
        }, ct);
        return ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> WorkspaceSymbolsAsync(
        string query, CancellationToken ct = default)
    {
        var result = await RequestAsync("workspace/symbol", new { query }, ct);
        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return [.. result.EnumerateArray().Select(symbol =>
        {
            var location = symbol.TryGetProperty("location", out var loc) ? loc : default;
            return new LspLocation(
                location.ValueKind == JsonValueKind.Object ? Str(location, "uri") ?? "" : "",
                location.ValueKind == JsonValueKind.Object && location.TryGetProperty("range", out var range)
                    ? ParseRange(range)
                    : new LspRange(0, 0, 0, 0),
                Str(symbol, "name"),
                Str(symbol, "containerName"));
        }).Where(location => location.Uri.Length > 0)];
    }

    public async Task<IReadOnlyList<LspDocumentSymbol>> DocumentSymbolsAsync(
        string uri, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/documentSymbol", new
        {
            textDocument = new { uri }
        }, ct);
        if (result.ValueKind != JsonValueKind.Array)
            return [];

        var symbols = new List<LspDocumentSymbol>();
        foreach (var symbol in result.EnumerateArray())
            ParseDocumentSymbol(symbol, null, symbols);
        return symbols;
    }

    private static void ParseDocumentSymbol(
        JsonElement symbol,
        string? containerName,
        ICollection<LspDocumentSymbol> symbols)
    {
        if (symbol.ValueKind != JsonValueKind.Object)
            return;

        var name = Str(symbol, "name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var kind = symbol.TryGetProperty("kind", out var kindElement)
                   && kindElement.ValueKind == JsonValueKind.Number
            ? kindElement.GetInt32()
            : 0;
        LspRange? range = null;
        LspRange? selectionRange = null;
        if (symbol.TryGetProperty("range", out var rangeElement)
            && rangeElement.ValueKind == JsonValueKind.Object)
            range = ParseRange(rangeElement);
        if (symbol.TryGetProperty("selectionRange", out var selectionElement)
            && selectionElement.ValueKind == JsonValueKind.Object)
            selectionRange = ParseRange(selectionElement);

        if (range is null
            && symbol.TryGetProperty("location", out var location)
            && location.ValueKind == JsonValueKind.Object
            && location.TryGetProperty("range", out var symbolInformationRange))
        {
            range = ParseRange(symbolInformationRange);
            selectionRange = range;
        }

        if (range is { } symbolRange)
        {
            symbols.Add(new LspDocumentSymbol(
                name,
                kind,
                symbolRange,
                selectionRange ?? symbolRange,
                Str(symbol, "containerName") ?? containerName));
        }

        var childContainerName = containerName is null ? name : $"{containerName}.{name}";
        if (symbol.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                ParseDocumentSymbol(child, childContainerName, symbols);
        }
    }

    public Task<JsonElement> RenameAsync(
        string uri, int line, int character, string newName, CancellationToken ct = default)
        => RequestAsync("textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line, character },
            newName
        }, ct);

    public async Task<IReadOnlyList<LspCodeAction>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri },
            range = ToLspRange(range),
            context = new
            {
                diagnostics = diagnostics.Select(diagnostic => diagnostic.Raw
                    ?? JsonSerializer.SerializeToElement(new
                    {
                        range = ToLspRange(new LspRange(
                            diagnostic.StartLine, diagnostic.StartChar,
                            diagnostic.EndLine, diagnostic.EndChar)),
                        severity = diagnostic.Severity,
                        message = diagnostic.Message
                    })).ToArray()
            }
        }, ct);

        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return [.. result.EnumerateArray().Select(ParseCodeAction)
            .Where(action => action is not null).Select(action => action!)];
    }

    public async Task<LspCodeAction> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct = default)
    {
        var result = await RequestAsync("codeAction/resolve", action.Raw, ct);
        return ParseCodeAction(result)
               ?? throw new LspException($"The language server returned an invalid resolved action for '{action.Title}'.");
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string uri,
        int tabSize,
        bool insertSpaces,
        CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/formatting", new
        {
            textDocument = new { uri },
            options = new { tabSize, insertSpaces }
        }, ct);

        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return [.. result.EnumerateArray()
            .Where(edit => edit.ValueKind == JsonValueKind.Object
                           && edit.TryGetProperty("range", out _)
                           && edit.TryGetProperty("newText", out _))
            .Select(edit => new LspTextEdit(
                ParseRange(edit.GetProperty("range")),
                Str(edit, "newText") ?? ""))];
    }

    public Task<JsonElement> ExecuteCommandAsync(
        string command,
        JsonElement? arguments,
        CancellationToken ct = default)
        => RequestAsync("workspace/executeCommand", new
        {
            command,
            arguments = arguments is { } values && values.ValueKind == JsonValueKind.Array
                ? values
                : JsonEmptyArray
        }, ct);

    public static LspCodeAction? ParseCodeAction(JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object || Str(action, "title") is not { } title)
            return null;

        if (action.TryGetProperty("disabled", out var disabled)
            && Str(disabled, "reason") is not null)
            return null;

        JsonElement? edit = action.TryGetProperty("edit", out var workspaceEdit)
            ? workspaceEdit.Clone()
            : null;
        JsonElement? command = null;
        if (action.TryGetProperty("command", out var commandElement))
        {
            if (commandElement.ValueKind == JsonValueKind.Object
                && Str(commandElement, "command") is { Length: > 0 })
                command = commandElement.Clone();
            else if (commandElement.ValueKind == JsonValueKind.String
                     && commandElement.GetString() is { Length: > 0 } legacyCommand)
            {
                var arguments = action.TryGetProperty("arguments", out var legacyArguments)
                    ? legacyArguments.Clone()
                    : JsonEmptyArray;
                command = JsonSerializer.SerializeToElement(new
                {
                    command = legacyCommand,
                    arguments
                });
            }
        }

        var kind = Str(action, "kind");
        var diagnosticMessages = action.TryGetProperty("diagnostics", out var diagnosticArray)
                                 && diagnosticArray.ValueKind == JsonValueKind.Array
            ? diagnosticArray.EnumerateArray()
                .Select(diagnostic => Str(diagnostic, "message"))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
        var description = diagnosticMessages.Length > 0
            ? $"Addresses: {string.Join("; ", diagnosticMessages)}"
            : kind switch
            {
                "quickfix" => "Quick fix for the issue at the current line or selection.",
                "refactor" or "refactor.extract" or "refactor.inline" or "refactor.rewrite"
                    => "Refactoring action for the current line or selection.",
                _ => "Language-server action for the current line or selection."
            };
        if (edit is not null)
            description += Environment.NewLine + "Applies text edits; the modified documents remain unsaved.";
        if (command is not null)
            description += Environment.NewLine + "Runs a language-server command.";

        return new LspCodeAction(title, kind, description, edit, command, action.Clone());
    }

    private static object ToLspRange(LspRange range) => new
    {
        start = new { line = range.StartLine, character = range.StartChar },
        end = new { line = range.EndLine, character = range.EndChar }
    };

    private static LspSignatureInformation ParseSignatureInformation(JsonElement signature)
    {
        var label = Str(signature, "label") ?? "";
        var parameters = signature.TryGetProperty("parameters", out var values)
                         && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(p => new LspParameterInformation(
                ReadParameterLabel(p, label),
                p.TryGetProperty("documentation", out var docs) ? NullIfEmpty(ReadMarkup(docs)) : null))
                .ToArray()
            : [];

        int? activeParameter = signature.TryGetProperty("activeParameter", out var index)
                               && index.ValueKind == JsonValueKind.Number
            ? index.GetInt32()
            : null;

        return new LspSignatureInformation(
            label,
            signature.TryGetProperty("documentation", out var documentation)
                ? NullIfEmpty(ReadMarkup(documentation))
                : null,
            parameters,
            activeParameter);
    }

    private static string ReadParameterLabel(JsonElement parameter, string signatureLabel)
    {
        if (!parameter.TryGetProperty("label", out var label))
            return "";
        if (label.ValueKind == JsonValueKind.String)
            return label.GetString() ?? "";
        if (label.ValueKind == JsonValueKind.Array
            && label.GetArrayLength() == 2
            && label[0].TryGetInt32(out var start)
            && label[1].TryGetInt32(out var end)
            && start >= 0 && end >= start && end <= signatureLabel.Length)
            return signatureLabel[start..end];
        return "";
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? "")
                .Where(value => value.Length > 0)
                .ToArray()
            : [];

    private static bool HasProvider(JsonElement capabilities, string name)
        => capabilities.TryGetProperty(name, out var provider)
           && provider.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);

    private static IReadOnlyList<LspLocation> ParseLocations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Array)
            return [.. result.EnumerateArray().Select(ParseLocationLink).OfType<LspLocation>()];
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("uri", out _))
            return [ParseLocation(result)];
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("targetUri", out _))
            return [ParseLocationLink(result)!];
        return [];
    }

    private static LspLocation ParseLocation(JsonElement location)
        => new(
            Str(location, "uri") ?? "",
            location.TryGetProperty("range", out var range)
                ? ParseRange(range)
                : new LspRange(0, 0, 0, 0));

    private static LspLocation? ParseLocationLink(JsonElement location)
    {
        if (location.TryGetProperty("targetUri", out var targetUri))
        {
            var range = location.TryGetProperty("targetSelectionRange", out var selectionRange)
                ? ParseRange(selectionRange)
                : location.TryGetProperty("targetRange", out var targetRange)
                    ? ParseRange(targetRange)
                    : new LspRange(0, 0, 0, 0);
            return new LspLocation(targetUri.GetString() ?? "", range);
        }

        return location.TryGetProperty("uri", out _)
            ? ParseLocation(location)
            : null;
    }

    private static LspCompletionItem ParseCompletionItem(JsonElement i)
    {
        LspTextEdit? edit = null;
        if (i.TryGetProperty("textEdit", out var te) && te.ValueKind == JsonValueKind.Object
            && te.TryGetProperty("newText", out var newText))
        {
            // TextEdit has "range"; InsertReplaceEdit has "insert" and "replace" (we want replace).
            JsonElement range = default;
            if (te.TryGetProperty("range", out var r1)) range = r1;
            else if (te.TryGetProperty("replace", out var r2)) range = r2;
            else if (te.TryGetProperty("insert", out var r3)) range = r3;

            if (range.ValueKind == JsonValueKind.Object)
                edit = new LspTextEdit(ParseRange(range), newText.GetString() ?? "");
        }

        return new LspCompletionItem(
            Label: i.GetProperty("label").GetString() ?? "",
            Kind: i.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 0,
            Detail: Str(i, "detail"),
            Documentation: i.TryGetProperty("documentation", out var d) ? NullIfEmpty(ReadMarkup(d)) : null,
            SortText: Str(i, "sortText"),
            FilterText: Str(i, "filterText"),
            InsertText: Str(i, "insertText"),
            Edit: edit);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static LspRange ParseRange(JsonElement r)
    {
        var s = r.GetProperty("start");
        var e = r.GetProperty("end");
        return new LspRange(
            s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32(),
            e.GetProperty("line").GetInt32(), e.GetProperty("character").GetInt32());
    }

    // MarkupContent { kind, value }, MarkedString { language, value }, a bare string, or an array of those.
    private static string ReadMarkup(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Object when Str(e, "language") is { } language
                                && Str(e, "value") is { } code
            => $"```{language}\n{code}\n```",
        JsonValueKind.Object => Str(e, "value") ?? "",
        JsonValueKind.Array => string.Join("\n\n", e.EnumerateArray().Select(ReadMarkup).Where(s => s.Length > 0)),
        _ => ""
    };

    private static (string Content, bool IsMarkdown) ReadHoverMarkup(JsonElement contents)
    {
        if (contents.ValueKind == JsonValueKind.Object
            && Str(contents, "kind") is { } kind
            && contents.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? "", kind == "markdown");

        var isMarkdown = contents.ValueKind is JsonValueKind.String or JsonValueKind.Array
                         || (contents.ValueKind == JsonValueKind.Object
                             && Str(contents, "language") is not null);
        return (ReadMarkup(contents), isMarkdown);
    }

    // --------------------------------------------------------------- cleanup

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _outbox.Writer.TryComplete();
        _cts.Cancel();

        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }

        _process.Dispose();
    }

}
