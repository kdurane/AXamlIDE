using System.Text.Json;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Preview;

/// <summary>
/// The designer's error as a squiggle in the editor: Avalonia's own XAML loader found it (a missing "/&gt;", an
/// unknown property), so it's the most reliable error there is for AXAML. It's marked so the Problems tab can leave
/// it out of the document's list: Problems shows it already, as the preview's own row.
/// </summary>
public static class PreviewErrorDiagnostic
{
    private const string Source = "preview";
    private static readonly JsonElement Raw = JsonDocument.Parse($"{{\"source\":\"{Source}\"}}").RootElement.Clone();

    /// <summary>
    /// The squiggle for <paramref name="error"/> in <paramref name="text"/>: the word at its line and column (one
    /// character when there's no word there), or the line when there's no column. Null when it has no line.
    /// </summary>
    public static LspDiagnostic? From(PreviewError error, string text)
    {
        if (error.Line is not { } errorLine)
            return null;

        // The line's start and end in the text, clamped to the last line.
        var line = 0;
        var lineStart = 0;
        while (line < errorLine - 1 && text.IndexOf('\n', lineStart) is var newline and >= 0)
        {
            lineStart = newline + 1;
            line++;
        }
        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            lineEnd--;

        int start, end;
        if (error.Column is { } column)
        {
            start = System.Math.Min(lineStart + column - 1, lineEnd);
            end = start;
            while (end < lineEnd && IsWordChar(text[end]))
                end++;
            if (end == start)
                end = System.Math.Min(start + 1, lineEnd);   // not on a word: the character there
        }
        else
        {
            start = lineStart;
            while (start < lineEnd && char.IsWhiteSpace(text[start]))
                start++;
            end = lineEnd;
        }

        return new LspDiagnostic(line, start - lineStart, line, end - lineStart, LspDiagnosticSeverity.Error,
            error.Message, Raw);
    }

    /// <summary>Whether a document diagnostic is the preview's error (made by <see cref="From"/>).</summary>
    public static bool IsFrom(JsonElement? raw)
        => raw is { ValueKind: JsonValueKind.Object } diagnostic
           && diagnostic.TryGetProperty("source", out var source)
           && source.ValueKind == JsonValueKind.String
           && source.GetString() == Source;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';
}
