using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Lsp;

/// <summary>Reading LSP JSON into the records in LspTypes.cs.</summary>
public sealed partial class LspClient
{
    internal static LspCodeAction? ParseCodeAction(JsonElement action)
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
            switch (commandElement.ValueKind)
            {
                case JsonValueKind.Object
                    when Str(commandElement, "command") is { Length: > 0 }:
                    command = commandElement.Clone();
                    break;
                case JsonValueKind.String
                    when commandElement.GetString() is { Length: > 0 } legacyCommand:
                    {
                        var arguments = action.TryGetProperty("arguments", out var legacyArguments)
                            ? legacyArguments.Clone()
                            : JsonEmptyArray;
                        command = JsonSerializer.SerializeToElement(new
                        {
                            command = legacyCommand,
                            arguments
                        });
                        break;
                    }
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
        return label.ValueKind switch
        {
            JsonValueKind.String => label.GetString() ?? "",
            JsonValueKind.Array when label.GetArrayLength() == 2 && label[0].TryGetInt32(out var start) &&
                                     label[1].TryGetInt32(out var end) && start >= 0 && end >= start &&
                                     end <= signatureLabel.Length => signatureLabel[start..end],
            _ => ""
        };
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
        switch (result.ValueKind)
        {
            case JsonValueKind.Array:
                return [.. result.EnumerateArray().Select(ParseLocationLink).OfType<LspLocation>()];
            case JsonValueKind.Object when result.TryGetProperty("uri", out _):
                return [ParseLocation(result)];
        }

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
}
