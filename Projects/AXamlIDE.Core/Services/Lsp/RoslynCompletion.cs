using System.Text.Json;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// The edit carried by Roslyn's completionComplexEdit command (see <see cref="RoslynCompletion"/>).
/// </summary>
/// <param name="Uri">The document it's for.</param>
/// <param name="Edit">The real edit, made after the item's own one (usually empty).</param>
/// <param name="IsSnippet">The new text is a snippet ($1, ${1:name}, $0).</param>
/// <param name="CaretOffset">Where the caret goes afterwards, as an offset into the edited document; -1 to leave it
/// at the end of the edit.</param>
public sealed record RoslynComplexEdit(string Uri, LspTextEdit Edit, bool IsSnippet, int CaretOffset);

/// <summary>
/// Roslyn's client-side completion command. Some completions are too big for a plain text edit (an override's whole
/// method, a partial method's signature), so the item's own edit is empty and its command,
/// roslyn.client.completionComplexEdit, carries the real one. The client makes that edit itself: sending the command
/// back with workspace/executeCommand fails, as with <see cref="RoslynCodeActions"/>.
/// </summary>
public static class RoslynCompletion
{
    public const string ComplexEditCommand = "roslyn.client.completionComplexEdit";

    /// <summary>
    /// The edit in a completionComplexEdit command, or null for any other command. Its arguments are
    /// [textDocument, textEdit, isSnippetString, newOffset].
    /// </summary>
    public static RoslynComplexEdit? ComplexEdit(JsonElement? command)
    {
        if (command is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("command", out var name) || name.GetString() != ComplexEditCommand
            || !value.TryGetProperty("arguments", out var arguments)
            || arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() < 2)
            return null;

        var document = arguments[0];
        var edit = arguments[1];
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("uri", out var uri)
            || edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("range", out var range)
            || !edit.TryGetProperty("newText", out var newText))
            return null;

        var isSnippet = arguments.GetArrayLength() > 2 && arguments[2].ValueKind == JsonValueKind.True;
        var caretOffset = arguments.GetArrayLength() > 3 && arguments[3].TryGetInt32(out var offset) ? offset : -1;
        return new RoslynComplexEdit(
            uri.GetString() ?? "",
            new LspTextEdit(LspHelpers.ParseLspRange(range), newText.GetString() ?? ""),
            isSnippet,
            caretOffset);
    }

    /// <summary>The name of a command (a Command object's "command"), or null.</summary>
    public static string? CommandName(JsonElement? command)
        => command is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    /// <summary>A command's arguments, or null when it has none.</summary>
    public static JsonElement? CommandArguments(JsonElement? command)
        => command is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array
            ? arguments
            : null;
}
