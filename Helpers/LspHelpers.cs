using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AXamlIDE.Lsp;

namespace AXamlIDE.Helpers
{
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

        public static string FormatLocation(LspLocation location)
        {
            var path = Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : location.Uri;
            return $"{Path.GetFileName(path)}  (line {location.Range.StartLine + 1})  —  {path}";
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
                if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
                    throw new InvalidOperationException($"Rename returned a non-file URI: {uriText}");
                if (edits.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Rename returned an invalid edit list.");

                var path = Path.GetFullPath(uri.LocalPath);
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
}