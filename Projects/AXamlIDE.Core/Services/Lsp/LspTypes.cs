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
public sealed record LspCompletionItem(string Label, int Kind, string? Detail, string? Documentation, string? SortText, string? FilterText, string? InsertText, LspTextEdit? Edit);
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
