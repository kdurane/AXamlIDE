using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Workspace;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// The IDE as an MCP server, so Claude (the Claude panel, or Claude Code in a terminal) can see what it can't get from
/// the files: a rendered preview of an AXAML file, the Problems list, and what's open in the editor. Started with the
/// app on <see cref="DefaultPort"/> (any free port when that's taken); the Claude panel is given <see cref="Url"/>
/// for each session. Tools run on the UI thread, where the documents and the workspace live.
///
/// Answers are kept short on purpose (paths relative to the workspace, one line per problem, pictures shrunk) since
/// everything a tool returns goes into the model's context.
/// </summary>
public sealed class IdeMcpServer : IDisposable
{
    /// <summary>For Claude Code: claude mcp add --transport http axamlide http://localhost:47613/mcp</summary>
    public const int DefaultPort = 47613;

    private const string Instructions =
        "Tools for the AXamlIDE workspace that's open. render_xaml shows what an .axaml file looks like (using the " +
        "editor's unsaved text), can hover or click a point first, and reports its XAML error if it can't render; " +
        "get_problems lists errors and warnings from the Problems panel; get_editor_state says which file is active, " +
        "where the caret is, and what's unsaved; read_open_file reads an open file as the editor has it, unsaved edits " +
        "included. Relative paths are from the workspace folder.";

    /// <summary>The Claude panel's sessions connect with this (?client=panel), so its switch can turn just them away.</summary>
    private const string PanelClient = "panel";

    private readonly WorkspaceModel _workspace;
    private readonly DocumentManager _documents;
    private readonly ProblemsViewModel _problems;
    private readonly McpPreviewRenderer _renderer;
    private readonly McpServer _server;

    public IdeMcpServer(WorkspaceModel workspace, DocumentManager documents, ProblemsViewModel problems, McpPreviewRenderer renderer,
        IEditorSettings settings)
    {
        _workspace = workspace;
        _documents = documents;
        _problems = problems;
        _renderer = renderer;
        var version = typeof(IdeMcpServer).Assembly.GetName().Version?.ToString() ?? "0";
        _server = new McpServer("axamlide", version, [RenderXaml(), GetProblems(), GetEditorState(), ReadOpenFile()], Instructions)
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

    // ------------------------------------------------------------ render_xaml

    private McpTool RenderXaml()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "The .axaml file (absolute, or relative to the workspace folder).", null),
            ("xaml", "string", "XAML to render in that file's place, to try a change without editing it. Default: the editor's text, or the file's.", null),
            ("theme", "string", "Wrap the content in this theme variant. Default: none (the app's).", ["Light", "Dark"]),
            ("scale", "number", "Render scaling, 1 = 96 DPI. Default 1.", null),
            ("maxSize", "number", "Shrink the picture to fit this many pixels on its longer side. Default 800.", null),
            ("pointer", "string", "Do this with the mouse at (x, y) before the picture is taken: hover (pointer-over styles, " +
                "tooltips stay closed), press (held down: pressed styles) or click (press and let go).", ["hover", "press", "click"]),
            ("x", "number", "The pointer's x in DIPs from the left (a pixel of an unshrunk render at scale 1).", null),
            ("y", "number", "The pointer's y in DIPs from the top.", null));
        schema["required"] = new JsonArray("path");
        return new McpTool("render_xaml",
            "Render an .axaml file with the Avalonia previewer and return it as a PNG, or its XAML error (with the line). " +
            "The project must have been built. Size the root with d:DesignWidth/d:DesignHeight; bindings show nothing " +
            "without Design.DataContext. The pointer only reaches Avalonia's input, not the OS window chrome.",
            schema,
            (arguments, ct) => OnUiThread(() => RenderXamlAsync(arguments, ct)));
    }

    private async Task<McpToolResult> RenderXamlAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ResolvePath(McpArguments.String(arguments, "path")) is not { } path)
            return McpToolResult.Error("Give the path of an .axaml file.");
        if (!path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Error($"{Relative(path)} isn't an .axaml file.");

        var open = FindOpen(path);
        var text = McpArguments.String(arguments, "xaml")
                   ?? open?.Document.Text
                   ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null);
        if (text is null)
            return McpToolResult.Error($"{Relative(path)} doesn't exist.");

        var theme = McpArguments.String(arguments, "theme") ?? "";
        var scale = Math.Clamp(McpArguments.Number(arguments, "scale") ?? 1, 0.25, 4);
        var maxSize = (int)Math.Clamp(McpArguments.Number(arguments, "maxSize") ?? 800, 64, 2000);

        McpPreviewRenderer.Pointer? pointer = null;
        if (McpArguments.String(arguments, "pointer") is { } action)
        {
            if (McpArguments.Number(arguments, "x") is not { } x || McpArguments.Number(arguments, "y") is not { } y)
                return McpToolResult.Error("A pointer action needs x and y.");
            pointer = new McpPreviewRenderer.Pointer(action, x, y);
        }

        var result = await _renderer.RenderAsync(path, PreviewThemeVariant.Apply(text, theme), scale, maxSize, pointer, ct);
        if (result.Error is { } error)
        {
            var where = error.Line is { } line ? $" (line {line}{(error.Column is { } column ? $", column {column}" : "")})" : "";
            return McpToolResult.Error($"{Relative(path)} didn't render{where}: {error.Message}");
        }

        var source = McpArguments.String(arguments, "xaml") is not null ? "the given XAML"
            : open is { IsDirty: true } ? "the editor's unsaved text"
            : "the saved file";
        return new McpToolResult([
            new McpContent.Text($"{Relative(path)} rendered from {source}, {result.Width}x{result.Height} px" +
                (pointer is null ? "." : $", after {pointer.Action} at ({pointer.X}, {pointer.Y}).")),
            new McpContent.Png(result.Png!)
        ]);
    }

    // ------------------------------------------------------------ get_problems

    private McpTool GetProblems()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "Only this file's problems (absolute or workspace-relative). Default: every file.", null),
            ("severity", "string", "The least severe to include. Default: warning.", ["error", "warning", "info"]),
            ("limit", "number", "At most this many lines. Default 50.", null));
        return new McpTool("get_problems",
            "The IDE's Problems list (language servers, the last build, the previewer), one per line: " +
            "file:line:column severity code message [source]. Covers open files and the last build's output.",
            schema,
            (arguments, _) => OnUiThread(() => Task.FromResult(ListProblems(arguments))));
    }

    private McpToolResult ListProblems(JsonElement arguments)
    {
        var path = ResolvePath(McpArguments.String(arguments, "path"));
        var least = McpArguments.String(arguments, "severity") switch
        {
            "error" => LspDiagnosticSeverity.Error,
            "info" => LspDiagnosticSeverity.Hint,
            _ => LspDiagnosticSeverity.Warning
        };
        var limit = (int)Math.Clamp(McpArguments.Number(arguments, "limit") ?? 50, 1, 500);

        var problems = _problems.All
            .Where(p => p.Severity <= least)
            .Where(p => path is null || p.Path.Length > 0 && GitStatus.PathComparer.Equals(Path.GetFullPath(p.Path), path))
            .OrderBy(p => p.Severity).ThenBy(p => p.Path).ThenBy(p => p.Line)
            .ToList();
        if (problems.Count == 0)
            return McpToolResult.Text(path is null ? "No problems." : $"No problems in {Relative(path)}.");

        var text = new StringBuilder();
        foreach (var p in problems.Take(limit))
        {
            var where = p.Path.Length == 0 ? "(project)" : p.Line > 0 ? $"{Relative(p.Path)}:{p.Line}:{p.Column}" : Relative(p.Path);
            var severity = p.IsError ? "error" : p.IsWarning ? "warning" : "info";
            var code = p.Code.Length > 0 ? p.Code + " " : "";
            text.Append(where).Append(' ').Append(severity).Append(' ').Append(code).Append(p.Message.ReplaceLineEndings(" "))
                .Append(" [").Append(p.Source).Append(']').AppendLine();
        }
        if (problems.Count > limit)
            text.AppendLine($"...and {problems.Count - limit} more.");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ get_editor_state

    private McpTool GetEditorState() => new("get_editor_state",
        "What the user has open: the workspace folder, the active file with its caret, and the open files (marked when unsaved).",
        McpArguments.Schema(),
        (_, _) => OnUiThread(() => Task.FromResult(DescribeEditor())));

    private McpToolResult DescribeEditor()
    {
        var text = new StringBuilder();
        text.AppendLine($"Workspace: {_workspace.RootDirectory ?? "(none open)"}");
        if (_documents.Active is { } active)
        {
            var caret = active.Document.GetLocation(Math.Min(active.CaretOffset, active.Document.TextLength));
            text.AppendLine($"Active: {Relative(active.Path)} (line {caret.Line}, column {caret.Column}){(active.IsDirty ? ", unsaved" : "")}");
        }
        else
            text.AppendLine("Active: none");

        var others = _documents.Documents.Where(d => !ReferenceEquals(d, _documents.Active)).ToList();
        if (others.Count > 0)
            text.AppendLine("Also open: " + string.Join(", ", others.Select(d => Relative(d.Path) + (d.IsDirty ? " (unsaved)" : ""))));
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ read_open_file

    private McpTool ReadOpenFile()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "The file (absolute or workspace-relative). Default: the active one.", null),
            ("startLine", "number", "First line, from 1. Default 1.", null),
            ("endLine", "number", "Last line. Default: 200 lines on from startLine.", null));
        return new McpTool("read_open_file",
            "A file open in the editor as the user sees it, unsaved edits included, with line numbers. " +
            "For a file that isn't open (or is saved) read it from disk instead.",
            schema,
            (arguments, _) => OnUiThread(() => Task.FromResult(ReadOpen(arguments))));
    }

    private McpToolResult ReadOpen(JsonElement arguments)
    {
        var document = ResolvePath(McpArguments.String(arguments, "path")) is { } path ? FindOpen(path) : _documents.Active;
        if (document is null)
            return McpToolResult.Error(McpArguments.String(arguments, "path") is { } asked ? $"{asked} isn't open in the editor." : "No file is active.");

        var text = document.Document;
        var first = (int)Math.Clamp(McpArguments.Number(arguments, "startLine") ?? 1, 1, Math.Max(1, text.LineCount));
        var last = (int)Math.Clamp(McpArguments.Number(arguments, "endLine") ?? first + 199, first, text.LineCount);
        var output = new StringBuilder();
        output.AppendLine($"{Relative(document.Path)}{(document.IsDirty ? " (unsaved)" : "")}, lines {first}-{last} of {text.LineCount}:");
        for (var number = first; number <= last; number++)
        {
            var line = text.GetLineByNumber(number);
            output.Append(number).Append('\t').AppendLine(text.GetText(line.Offset, line.Length));
        }
        return McpToolResult.Text(output.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ helpers

    private static Task<McpToolResult> OnUiThread(Func<Task<McpToolResult>> run)
        => Dispatcher.UIThread.InvokeAsync(run);

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
}
