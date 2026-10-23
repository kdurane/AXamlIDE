using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
// ReSharper disable SuggestVarOrType_SimpleTypes

namespace AXamlIDE.Services.Lsp;

public sealed class LspException(string message) : Exception(message);

/// <summary>
/// A small, dependency-free LSP client: JSON-RPC 2.0 over stdio with Content-Length
/// framing. One long-lived server process, one reader loop, one writer loop (a
/// channel, so notifications always go out in the order they were queued -
/// didChange ordering matters).
///
/// Split across files: this one (state, start-up handshake, document notifications, Dispose),
/// LspClient.Transport.cs, LspClient.Incoming.cs, LspClient.Requests.cs, LspClient.Parsing.cs.
/// </summary>
public sealed partial class LspClient : ILspSender, IDisposable
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

    /// <summary>textDocument/codeLens: the "N references" lines above declarations.</summary>
    public bool SupportsCodeLens { get; private set; }

    /// <summary>completionItem/resolve: an item's documentation and extra edits (the using to add) on request.</summary>
    public bool SupportsCompletionResolve { get; private set; }

    /// <summary>textDocument/semanticTokens/range: what each name in some lines is (class, parameter, local...).</summary>
    public bool SupportsSemanticTokens { get; private set; }

    /// <summary>The token type names semantic tokens refer to by index (the server's legend).</summary>
    public IReadOnlyList<string> SemanticTokenTypes { get; private set; } = [];

    /// <summary>The modifier names semantic tokens' modifier bits stand for, lowest bit first.</summary>
    public IReadOnlyList<string> SemanticTokenModifiers { get; private set; } = [];

    /// <summary>textDocument/inlayHint: parameter names and inferred types shown in the code.</summary>
    public bool SupportsInlayHints { get; private set; }

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
    private bool _staticCodeLens;

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

    /// <summary>The server's code lenses changed (e.g. references in another file): ask again. Background thread.</summary>
    public event Action? CodeLensRefreshRequested;

    /// <summary>The server's semantic tokens changed (its projects loaded, another file changed): ask again. Background thread.</summary>
    public event Action? SemanticTokensRefreshRequested;

    /// <summary>The server's inlay hints changed: ask again. Background thread.</summary>
    public event Action? InlayHintsRefreshRequested;

    /// <summary>
    /// The server has just been given its options (workspace/configuration), at start-up or after
    /// <see cref="DidChangeConfiguration"/>. Roslyn doesn't say when that changes its inlay hints, so ask again on this.
    /// Background thread.
    /// </summary>
    public event Action? ConfigurationAnswered;
    public Func<JsonElement, Task<bool>>? WorkspaceEditHandler { get; set; }

    /// <summary>Answers the server's workspace/configuration questions by section name (see <see cref="LspServerConfig.Settings"/>).</summary>
    private readonly Func<string, object?>? _settings;

    /// <summary>Current work-done progress text ("Loading projects: ..."), or null when idle. Background thread.</summary>
    public event Action<string?>? Progress;

    private LspClient(Process process, Func<string, object?>? settings)
    {
        _process = process;
        _settings = settings;
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

    /// <param name="settings">Answers the server's workspace/configuration questions (it may ask straight after initialize).</param>
    public static async Task<LspClient> StartAsync(
        string command, IEnumerable<string> args, string rootPath, Func<string, object?>? settings = null, CancellationToken ct = default)
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

        var client = new LspClient(process, settings);
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

    private readonly string[] _hoverContext = ["markdown", "plaintext"];

    private readonly string[] _codeActions =
    [
        "", "quickfix", "refactor", "refactor.extract", "refactor.inline",
        "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"
    ];

    private readonly string[] _supports = ["edit", "command"];

    // What may be left out of completion items until they're resolved: the server fills it in for the one picked.
    private readonly string[] _completionResolve = ["documentation", "detail", "additionalTextEdits", "command", "textEdit"];

    private readonly string[] _relativeTokens = ["relative"];

    private async Task InitializeAsync(string rootPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var rootUri = new Uri(rootPath).AbsoluteUri;

        var result = await RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "AXamlIDE", version = "0.1" },
            rootUri,
            workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(rootPath) } },
            capabilities = new
            {
                workspace = new
                {
                    workspaceFolders = true,
                    configuration = true,
                    diagnostics = new { refreshSupport = true },
                    codeLens = new { refreshSupport = true },
                    semanticTokens = new { refreshSupport = true },
                    inlayHint = new { refreshSupport = true },
                    applyEdit = true,
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        // Creating files: GlobalSuppressions.cs, .editorconfig ("Suppress or configure issues")
                        resourceOperations = new[] { "create" },
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
                                valueSet = _codeActions
                            }
                        },
                        resolveSupport = new { properties = _supports }
                    },
                    hover = new { contentFormat = _hoverContext },
                    codeLens = new { dynamicRegistration = true },
                    completion = new
                    {
                        completionItem = new
                        {
                            snippetSupport = false,
                            labelDetailsSupport = true,
                            resolveSupport = new { properties = _completionResolve }
                        }
                    },
                    // Ranges only: we ask about the lines on screen. The types and modifiers we know are whatever the
                    // server's legend says (none listed here); syntax colouring stays TextMate's.
                    semanticTokens = new
                    {
                        requests = new { range = true, full = false },
                        tokenTypes = Array.Empty<string>(),
                        tokenModifiers = Array.Empty<string>(),
                        formats = _relativeTokens,
                        augmentsSyntaxTokens = true
                    },
                    inlayHint = new { },
                    signatureHelp = new
                    {
                        dynamicRegistration = false,
                        contextSupport = true,
                        signatureInformation = new
                        {
                            documentationFormat = _hoverContext,
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
                CompletionTriggerCharacters =
                [.. trig.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0)];
            }
            SupportsCompletionResolve = comp.ValueKind == JsonValueKind.Object
                                        && comp.TryGetProperty("resolveProvider", out var completionResolve)
                                        && completionResolve.ValueKind == JsonValueKind.True;

            // { legend: { tokenTypes, tokenModifiers }, range, full }
            if (caps.TryGetProperty("semanticTokensProvider", out var semanticTokens)
                && semanticTokens.ValueKind == JsonValueKind.Object
                && semanticTokens.TryGetProperty("legend", out var legend)
                && HasProvider(semanticTokens, "range"))
            {
                SemanticTokenTypes = ReadStringArray(legend, "tokenTypes");
                SemanticTokenModifiers = ReadStringArray(legend, "tokenModifiers");
                SupportsSemanticTokens = SemanticTokenTypes.Count > 0;
            }

            SupportsInlayHints = HasProvider(caps, "inlayHintProvider");

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
            _staticCodeLens = HasProvider(caps, "codeLensProvider");
            SupportsDefinition = _staticDefinition || _dynamicCapabilities.Contains("textDocument/definition");
            SupportsReferences = _staticReferences || _dynamicCapabilities.Contains("textDocument/references");
            SupportsRename = _staticRename || _dynamicCapabilities.Contains("textDocument/rename");
            SupportsWorkspaceSymbols = _staticWorkspaceSymbols || _dynamicCapabilities.Contains("workspace/symbol");
            SupportsDocumentSymbols = _staticDocumentSymbols || _dynamicCapabilities.Contains("textDocument/documentSymbol");
            SupportsCodeActions = _staticCodeActions || _dynamicCapabilities.Contains("textDocument/codeAction");
            SupportsCodeActionResolve = _staticCodeActionResolve || _dynamicCodeActionResolve;
            SupportsDocumentFormatting = _staticDocumentFormatting
                                         || _dynamicCapabilities.Contains("textDocument/formatting");
            SupportsCodeLens = _staticCodeLens || _dynamicCapabilities.Contains("textDocument/codeLens");

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

    /// <summary>The settings changed: the server asks for the ones it uses again (workspace/configuration).</summary>
    public void DidChangeConfiguration()
        => Notify("workspace/didChangeConfiguration", new { settings = (object?)null });

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
