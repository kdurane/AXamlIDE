using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AXamlIDE.Services.Lsp;

public static class LspHelpers
{
    public static bool RangesOverlap(LspRange first, LspRange second)
    {
        var firstStart = (first.StartLine, first.StartChar);
        var firstEnd = (first.EndLine, first.EndChar);
        var secondStart = (second.StartLine, second.StartChar);
        var secondEnd = (second.EndLine, second.EndChar);
        return Compare(firstStart, secondEnd) <= 0 && Compare(secondStart, firstEnd) <= 0;

        static int Compare((int Line, int Character) a, (int Line, int Character) b)
            => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);
    }

    /// <summary>
    /// The diagnostic carries this <see cref="LspDiagnosticTag"/>. Roslyn also puts Visual Studio's own tags in the
    /// same array (large numbers near int.MaxValue); they're simply never asked about.
    /// </summary>
    public static bool HasDiagnosticTag(JsonElement? rawDiagnostic, int tag)
    {
        if (rawDiagnostic is not { ValueKind: JsonValueKind.Object } raw
            || !raw.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in tags.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value) && value == tag)
                return true;
        return false;
    }

    /// <summary>A diagnostic's code ("CS0103", "IDE0005", "AVLN2000"), or empty when it has none.</summary>
    public static string DiagnosticCode(JsonElement? rawDiagnostic)
    {
        if (rawDiagnostic is not { } raw || raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("code", out var code))
            return "";
        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString() ?? "",
            JsonValueKind.Number => code.GetRawText(),
            _ => ""
        };
    }

    /// <summary>
    /// A diagnostic's message without the "CODE: " the client puts in front (see LspClient.ParseDiagnostic), for
    /// listing the code in its own column.
    /// </summary>
    public static string DiagnosticText(string message, string code)
        => code.Length > 0 && message.StartsWith(code + ": ", StringComparison.Ordinal) ? message[(code.Length + 2)..] : message;

    public static string FormatLocation(LspLocation location)
    {
        if (IsSourceGenerated(location.Uri))
            return $"{SourceGeneratedName(location.Uri)}  (line {location.Range.StartLine + 1})  —  source generated";
        var path = FileUris.ToPath(location.Uri) ?? location.Uri;
        return $"{Path.GetFileName(path)}  (line {location.Range.StartLine + 1})  —  {path}";
    }

    /// <summary>
    /// Roslyn's URI for a source generator's output (roslyn-source-generated://{project}/{hint}.g.cs?documentId=...):
    /// not on disk; its text comes from <see cref="LspClient.SourceGeneratedTextAsync"/>.
    /// </summary>
    public static bool IsSourceGenerated(string uri) => uri.StartsWith("roslyn-source-generated://", StringComparison.Ordinal);

    /// <summary>The generated file's name (its hint name, e.g. "MyApp.ViewModels.MainViewModel.g.cs").</summary>
    public static string SourceGeneratedName(string uri)
    {
        var path = uri.Split('?')[0];
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }

    public static Dictionary<string, List<(LspRange Range, string NewText)>> ParseWorkspaceEdit(JsonElement workspaceEdit)
    {
        var editsByPath = new Dictionary<string, List<(LspRange, string)>>(StringComparer.OrdinalIgnoreCase);

        if (workspaceEdit.ValueKind != JsonValueKind.Object)
            return editsByPath;

        if (workspaceEdit.TryGetProperty("changes", out var changes)
            && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in changes.EnumerateObject())
                AddEdits(file.Name, file.Value);
        }

        if (!workspaceEdit.TryGetProperty("documentChanges", out var documentChanges)
            || documentChanges.ValueKind != JsonValueKind.Array) return editsByPath;
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                // A file to create comes before its edits (see FilesToCreate); renames and deletes aren't offered to servers
                if (change.TryGetProperty("kind", out var kind) && kind.GetString() == "create")
                    continue;
                if (!change.TryGetProperty("textDocument", out var textDocument)
                    || !textDocument.TryGetProperty("uri", out var uri)
                    || !change.TryGetProperty("edits", out var edits))
                    throw new InvalidOperationException("Rename returned an unsupported resource operation.");
                AddEdits(uri.GetString() ?? "", edits);
            }
        }

        return editsByPath;

        void AddEdits(string uriText, JsonElement edits)
        {
            if (FileUris.ToPath(uriText) is not { } localPath)
                throw new InvalidOperationException($"The edit is for a non-file URI: {uriText}");
            if (edits.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The edit has an invalid edit list.");

            var path = Path.GetFullPath(localPath);
            if (!editsByPath.TryGetValue(path, out var pathEdits))
                editsByPath[path] = pathEdits = [];
            foreach (var edit in edits.EnumerateArray())
            {
                if (!edit.TryGetProperty("range", out var range)
                    || !edit.TryGetProperty("newText", out var newText))
                    throw new InvalidOperationException("Rename returned an unsupported text edit.");
                pathEdits.Add((ParseLspRange(range), newText.GetString() ?? ""));
            }
        }
    }

    /// <summary>
    /// The files a workspace edit creates (its "create" resource operations), as local paths: Roslyn's
    /// "Suppress in Suppression File" creates GlobalSuppressions.cs, "Configure severity" an .editorconfig.
    /// The text edits for them follow in the same edit.
    /// </summary>
    public static IReadOnlyList<string> FilesToCreate(JsonElement workspaceEdit)
    {
        if (workspaceEdit.ValueKind != JsonValueKind.Object
            || !workspaceEdit.TryGetProperty("documentChanges", out var documentChanges)
            || documentChanges.ValueKind != JsonValueKind.Array)
            return [];

        var paths = new List<string>();
        foreach (var change in documentChanges.EnumerateArray())
            if (change.TryGetProperty("kind", out var kind) && kind.GetString() == "create"
                && change.TryGetProperty("uri", out var uriText)
                && FileUris.ToPath(uriText.GetString()) is { } path)
                paths.Add(Path.GetFullPath(path));
        return paths;
    }

    public static LspRange ParseLspRange(JsonElement range)
    {
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        return new LspRange(
            start.GetProperty("line").GetInt32(),
            start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(),
            end.GetProperty("character").GetInt32());
    }
}
