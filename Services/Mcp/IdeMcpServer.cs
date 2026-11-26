using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Workspace;
using AXamlIDE.Services.Xaml;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// The IDE as an MCP server, so Claude (the Claude panel, or Claude Code in a terminal) can see and ask what it can't
/// get from the files: a rendered AXAML preview, the Problems list, what's open in the editor, the C# language
/// server's answers (symbols, definitions, references, types), the debugger while it's paused, and the Output and
/// Tests panels. Started with the app on <see cref="DefaultPort"/> (any free port when that's taken); the Claude
/// panel is given <see cref="PanelUrl"/> for each session. Tools run on the UI thread, where the documents and the
/// workspace live.
///
/// The tools are grouped by what they look at, one partial file each: IdeMcpServer.Editor.cs (open files, the
/// selection, unsaved edits), .Preview.cs (render_xaml), .Ui.cs (ui_open, ui_act...: the app running headless), .Problems.cs (problems, check_file, build), .Code.cs (the
/// language server), .Debug.cs (the paused debugger) and .Output.cs (Output and Tests).
///
/// Answers are kept short on purpose (paths relative to the workspace, one line per item, pictures shrunk) since
/// everything a tool returns goes into the model's context.
/// </summary>
public sealed partial class IdeMcpServer : IDisposable
{
    /// <summary>For Claude Code: claude mcp add --transport http axamlide http://localhost:47613/mcp</summary>
    public const int DefaultPort = 47613;

    private const string Instructions =
        "Tools for the AXamlIDE workspace that's open: what the IDE knows that the files don't say. " +
        "Editor: get_editor_state (active file, caret, selection, unsaved files), read_open_file, get_unsaved_changes. " +
        "Code (C# language server, already loaded; better than grep for overloads, partial classes and generated code): " +
        "find_symbol, inspect_symbol (type, signature, docs and definition), find_references. Point at code by path, " +
        "line and the name as written on that line. " +
        "Checking: check_file (fresh errors for one file, seconds, no build), get_problems, build (the IDE's build, " +
        "which also updates the previews). " +
        "Looking: render_xaml (an .axaml file as a picture, or its XAML error). " +
        "Trying the UI: ui_open runs a view live in a hidden headless window and gives its control tree as text; ui_act " +
        "clicks, types and presses keys in it and says what changed; ui_tree and ui_screenshot look again. Prefer these to " +
        "pictures for checking behaviour: text is cheaper and exact. " +
        "Debugging: get_debug_state and evaluate_expression while the user's debugger is paused. " +
        "Output: get_output (build log, the running app's console), get_test_results. " +
        "Relative paths are from the workspace folder; lines and columns count from 1.";

    /// <summary>The Claude panel's sessions connect with this (?client=panel), so its switch can turn just them away.</summary>
    private const string PanelClient = "panel";

    private readonly WorkspaceModel _workspace;
    private readonly DocumentManager _documents;
    private readonly IDocumentHost _host;
    private readonly ProblemsViewModel _problems;
    private readonly McpPreviewRenderer _renderer;
    private readonly McpUiHost _uiHost;
    private readonly LanguageSessionService _languages;
    private readonly LspManager _servers;
    private readonly SolutionAnalysisService _solutionAnalysis;
    private readonly XamlLanguageService _xaml;
    private readonly DebugSnapshotTracker _debug;
    private readonly RunViewModel _run;
    private readonly TestExplorerViewModel _tests;
    private readonly McpServer _server;

    public IdeMcpServer(WorkspaceModel workspace, DocumentManager documents, IDocumentHost host, ProblemsViewModel problems, McpPreviewRenderer renderer, McpUiHost uiHost,
        LanguageSessionService languages, LspManager servers, SolutionAnalysisService solutionAnalysis, XamlLanguageService xaml,
        DebugSnapshotTracker debug, RunViewModel run, TestExplorerViewModel tests, IEditorSettings settings)
    {
        _workspace = workspace;
        _documents = documents;
        _host = host;
        _problems = problems;
        _renderer = renderer;
        _uiHost = uiHost;
        _languages = languages;
        _servers = servers;
        _solutionAnalysis = solutionAnalysis;
        _xaml = xaml;
        _debug = debug;
        _run = run;
        _tests = tests;
        var version = typeof(IdeMcpServer).Assembly.GetName().Version?.ToString() ?? "0";
        _server = new McpServer("axamlide", version,
        [
            GetEditorState(), ReadOpenFile(), GetUnsavedChanges(),
            FindSymbol(), InspectSymbol(), FindReferences(),
            CheckFile(), GetProblems(), Build(),
            RenderXaml(),
            UiOpen(), UiTree(), UiAct(), UiScreenshot(),
            GetDebugState(), EvaluateExpression(),
            GetOutput(), GetTestResults()
        ], Instructions)
        {
            // Read on each call, so the switch works at once, in the conversation that's going on too.
            RefuseCall = client => client == PanelClient && !settings.Features.ClaudeIdeTools
                ? "The user has turned the IDE tools off in the Claude panel. Don't call axamlide tools again in this conversation."
                : null
        };
    }

    /// <summary>http://localhost:port/mcp while it's listening, else null.</summary>
    public string? Url => _server.Url;

    /// <summary>What the Claude panel's sessions are given: <see cref="Url"/> marked as the panel's, or null.</summary>
    public string? PanelUrl => Url is { } url ? $"{url}?client={PanelClient}" : null;

    public bool Start() => _server.Start(DefaultPort);

    public void Dispose() => _server.Dispose();

    // ------------------------------------------------------------ helpers

    private static Task<McpToolResult> OnUiThread(Func<Task<McpToolResult>> run)
        => Dispatcher.UIThread.InvokeAsync(run);

    private static Task<McpToolResult> OnUiThread(Func<McpToolResult> run)
        => Dispatcher.UIThread.InvokeAsync(() => Task.FromResult(run()));

    private string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!Path.IsPathRooted(path) && _workspace.RootDirectory is { } root)
            path = Path.Combine(root, path);
        return Path.GetFullPath(path);
    }

    private string Relative(string path)
        => _workspace.RootDirectory is { } root && path.StartsWith(root, StringComparison.Ordinal)
            ? Path.GetRelativePath(root, path)
            : path;

    private OpenDocument? FindOpen(string path)
        => _documents.Documents.FirstOrDefault(d => GitStatus.PathComparer.Equals(Path.GetFullPath(d.Path), path));

    /// <summary>A file's text as the editor has it when it's open (unsaved edits included), else from disk; null if neither.</summary>
    private async Task<string?> TextOfAsync(string path, CancellationToken ct)
        => FindOpen(path)?.Document.Text ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null);
}
