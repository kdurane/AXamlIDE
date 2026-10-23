using System.Collections.Generic;
using System.Text.Json;

namespace AXamlIDE.Services.Lsp;

// All positions are LSP style: 0-based line, 0-based UTF-16 character.
public sealed record ContentChange(int StartLine, int StartChar, int EndLine, int EndChar, string Text);

/// <summary><c>Severity</c> is one of the <see cref="LspDiagnosticSeverity"/> values.</summary>
public sealed record LspDiagnostic(
    int StartLine,
    int StartChar,
    int EndLine,
    int EndChar,
    int Severity,
    string Message,
    JsonElement? Raw = null);

public sealed record LspRange(int StartLine, int StartChar, int EndLine, int EndChar);
public sealed record LspLocation(string Uri, LspRange Range, string? Name = null, string? ContainerName = null);
public sealed record LspDocumentSymbol(
    string Name,
    int Kind,
    LspRange Range,
    LspRange SelectionRange,
    string? ContainerName = null);
/// <summary>
/// A code lens: a line of text shown above <paramref name="Range"/> ("3 references"). <paramref name="Title"/> is null
/// until resolved; <paramref name="Raw"/> is the server's object, sent back as-is to resolve it.
/// </summary>
public sealed record LspCodeLens(LspRange Range, string? Title, System.Text.Json.JsonElement Raw);
public sealed record LspHover(string Text, LspRange? Range, bool IsMarkdown);
public sealed record LspTextEdit(LspRange Range, string NewText);
/// <summary>One completion. Some of it may only arrive once resolved (completionItem/resolve): the documentation, and
/// the edits elsewhere in the file.</summary>
/// <param name="LabelDescription">Shown dimmed after the label: for a type from a namespace that isn't imported yet, the
/// namespace ("System.Text").</param>
/// <param name="AdditionalEdits">Made along with the completion, elsewhere in the file: the using for that namespace.</param>
/// <param name="Command">Run after inserting it (Roslyn's override completion, see <see cref="RoslynCompletion"/>).</param>
/// <param name="Raw">The server's object, sent back as-is to resolve it.</param>
public sealed record LspCompletionItem(
    string Label,
    int Kind,
    string? Detail,
    string? Documentation,
    string? SortText,
    string? FilterText,
    string? InsertText,
    LspTextEdit? Edit,
    string? LabelDescription = null,
    IReadOnlyList<LspTextEdit>? AdditionalEdits = null,
    JsonElement? Command = null,
    JsonElement? Raw = null);

/// <summary>A completion list. Incomplete means the server left items out (too many, or still loading): ask again as
/// more of the word is typed.</summary>
public sealed record LspCompletionList(IReadOnlyList<LspCompletionItem> Items, bool IsIncomplete);

/// <summary>
/// A semantic token: what the code at a place is (a class, a parameter, a local...), which plain syntax colouring
/// can't tell. <paramref name="Type"/> is one of the server's token type names ("class", "parameter"...);
/// <paramref name="Modifiers"/> is a bit set over the server's modifier names ("static"...).
/// </summary>
public readonly record struct LspSemanticToken(int Line, int Character, int Length, string Type, int Modifiers);

/// <summary>
/// An inlay hint: text shown in the code that isn't in the file, such as a parameter name before an argument
/// ("count:") or the type of a var. Shown before the character at <paramref name="Line"/>, <paramref name="Character"/>.
/// </summary>
public sealed record LspInlayHint(int Line, int Character, string Label, bool PaddingLeft, bool PaddingRight);
public sealed record LspParameterInformation(string Label, string? Documentation);
public sealed record LspSignatureInformation(
    string Label,
    string? Documentation,
    IReadOnlyList<LspParameterInformation> Parameters,
    int? ActiveParameter);
public sealed record LspSignatureHelp(
    IReadOnlyList<LspSignatureInformation> Signatures,
    int ActiveSignature,
    int? ActiveParameter);
public sealed record LspCodeAction(
    string Title,
    string? Kind,
    string Description,
    JsonElement? Edit,
    JsonElement? Command,
    JsonElement Raw);
