using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Services.Mcp;

/// <summary>The editor's tools: what's open, where the caret and selection are, and the unsaved text.</summary>
public sealed partial class IdeMcpServer
{
    /// <summary>A selection longer than this is cut (it's in the editor; read_open_file can read the rest).</summary>
    private const int MaxSelectionLength = 4000;

    // ------------------------------------------------------------ get_editor_state

    private McpTool GetEditorState() => new("get_editor_state",
        "What the user has open: the workspace folder, the active file with its caret and selected text (what " +
        "\"this\" or \"here\" usually means), and the other open files (marked when unsaved).",
        McpArguments.Schema(),
        (_, _) => OnUiThread(DescribeEditor));

    private McpToolResult DescribeEditor()
    {
        var text = new StringBuilder();
        text.AppendLine($"Workspace: {_workspace.RootDirectory ?? "(none open)"}");
        if (_documents.Active is { } active)
        {
            var document = active.Document;
            var caret = document.GetLocation(Math.Min(active.CaretOffset, document.TextLength));
            text.AppendLine($"Active: {Relative(active.Path)} (line {caret.Line}, column {caret.Column}){(active.IsDirty ? ", unsaved" : "")}");

            var (offset, length) = active.Selection;
            if (length > 0 && offset + length <= document.TextLength)
            {
                var start = document.GetLocation(offset);
                var end = document.GetLocation(offset + length);
                var selected = document.GetText(offset, Math.Min(length, MaxSelectionLength));
                text.AppendLine($"Selected: line {start.Line}, column {start.Column} to line {end.Line}, column {end.Column}" +
                                (length > MaxSelectionLength ? $" ({length} characters, the first {MaxSelectionLength} shown)" : "") + ":");
                text.AppendLine(selected);
            }
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
            (arguments, _) => OnUiThread(() => ReadOpen(arguments)));
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

    // ------------------------------------------------------------ get_unsaved_changes

    /// <summary>Unchanged lines shown around each change.</summary>
    private const int DiffContext = 2;

    private McpTool GetUnsavedChanges()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "Only this file (absolute or workspace-relative). Default: every open file with unsaved edits.", null));
        return new McpTool("get_unsaved_changes",
            "What the user has typed but not saved: a diff of each unsaved open file against the file on disk, with the " +
            "editor's line numbers (\"-\" saved, \"+\" in the editor). Cheaper than reading the whole file.",
            schema,
            (arguments, ct) => OnUiThread(() => UnsavedChangesAsync(arguments, ct)));
    }

    private async Task<McpToolResult> UnsavedChangesAsync(JsonElement arguments, CancellationToken ct)
    {
        IEnumerable<OpenDocument> documents;
        if (ResolvePath(McpArguments.String(arguments, "path")) is { } path)
        {
            if (FindOpen(path) is not { } document)
                return McpToolResult.Error($"{Relative(path)} isn't open in the editor.");
            documents = [document];
        }
        else
            documents = _documents.Documents.Where(d => d.IsDirty && !d.IsReadOnly).ToList();

        var output = new StringBuilder();
        foreach (var document in documents)
        {
            var saved = File.Exists(document.Path) ? await File.ReadAllTextAsync(document.Path, ct) : null;
            output.AppendLine(saved is null ? $"{Relative(document.Path)}: not saved yet (new file)." : Diff(document, saved));
        }
        return McpToolResult.Text(output.Length == 0 ? "No unsaved changes." : output.ToString().TrimEnd());
    }

    /// <summary>A unified diff's hunks, each headed by the editor's line where it starts.</summary>
    private string Diff(OpenDocument document, string saved)
    {
        var lines = InlineDiff.Compare(saved.ReplaceLineEndings("\n"), document.Document.Text.ReplaceLineEndings("\n"));
        var output = new StringBuilder();
        var changed = lines.Select((line, index) => (line, index)).Where(x => x.line.Kind != DiffLineKind.Unchanged).Select(x => x.index).ToList();
        if (changed.Count == 0)
            return $"{Relative(document.Path)}: no differences from the saved file.";

        // The editor's line number of each diff line (a removed line takes the number of the line after it).
        var numbers = new int[lines.Count];
        var number = 1;
        for (var i = 0; i < lines.Count; i++)
        {
            numbers[i] = number;
            if (lines[i].Kind != DiffLineKind.Removed)
                number++;
        }

        output.AppendLine($"{Relative(document.Path)} (unsaved):");
        var start = 0;
        while (start < changed.Count)
        {
            var end = start;   // changes this close together share a hunk
            while (end + 1 < changed.Count && changed[end + 1] - changed[end] <= DiffContext * 2 + 1)
                end++;
            var from = Math.Max(0, changed[start] - DiffContext);
            var to = Math.Min(lines.Count - 1, changed[end] + DiffContext);
            output.AppendLine($"@@ line {numbers[from]}");
            for (var i = from; i <= to; i++)
            {
                var marker = lines[i].Kind switch { DiffLineKind.Added => '+', DiffLineKind.Removed => '-', _ => ' ' };
                output.Append(marker).AppendLine(lines[i].Text);
            }
            start = end + 1;
        }
        return output.ToString().TrimEnd();
    }
}
