using System;
using AXamlIDE.CompletionEngine.Parsing;

namespace AXamlIDE.Services.Indentation;

/// <summary>
/// Where a line of XAML should start, worked out from the elements still open above it:
/// one level inside the parent element, a closing tag level with its opening tag, an attribute on its own
/// line lined up with the attribute above it.
/// </summary>
public static class XamlIndentation
{
    /// <summary>The indentation the line starting at <paramref name="lineStart"/> should have.</summary>
    /// <param name="indentUnit">One level: "\t" or some spaces.</param>
    public static string IndentFor(string text, int lineStart, string indentUnit)
    {
        var parser = XmlParser.Parse(text.AsMemory(0, lineStart));
        var line = text.AsSpan(lineStart, LineEnd(text, lineStart) - lineStart).TrimStart();

        switch (parser.State)
        {
            case XmlParser.ParserState.InsideComment:
            case XmlParser.ParserState.InsideCdata:
            case XmlParser.ParserState.InsideDeclaration:
            case XmlParser.ParserState.AttributeValue:
                // Free text: keep whatever the line above had
                return lineStart == 0 ? "" : IndentOfLine(text, LineStartOf(text, lineStart - 1));

            case XmlParser.ParserState.StartElement:
            case XmlParser.ParserState.InsideElement:
            case XmlParser.ParserState.StartAttribute:
            case XmlParser.ParserState.BeforeAttributeValue:
            case XmlParser.ParserState.AfterAttributeValue:
                return AttributeIndent(text, parser.ContainingTagStart, indentUnit);

            default:
                if (parser.NestingLevel == 0)
                    return "";
                var parentIndent = IndentOfLine(text, LineStartOf(text, parser.ContainingTagStart));
                // </Grid> lines up with <Grid>; anything else goes one level inside it
                return line.StartsWith("</") ? parentIndent : parentIndent + indentUnit;
        }
    }

    /// <summary>
    /// The line starts with the closing tag of an element opened at the end of the line above
    /// ("&lt;Grid&gt;" then "&lt;/Grid&gt;"), as after pressing Enter between them. The caret then gets a line of its own.
    /// </summary>
    public static bool IsBetweenEmptyTagPair(string text, int lineStart)
    {
        var line = text.AsSpan(lineStart, LineEnd(text, lineStart) - lineStart).TrimStart();
        if (!line.StartsWith("</") || lineStart == 0)
            return false;

        var parser = XmlParser.Parse(text.AsMemory(0, lineStart));
        if (parser.State != XmlParser.ParserState.None || parser.NestingLevel == 0)
            return false;

        var openStart = parser.ContainingTagStart;
        var openEnd = lineStart - 1;
        while (openEnd > openStart && char.IsWhiteSpace(text[openEnd]))
            openEnd--;
        // Only whitespace between the opening tag's '>' and this line, and no other tag in between
        if (text[openEnd] != '>' || text.IndexOf('<', openStart + 1, openEnd - openStart) >= 0)
            return false;

        var name = XamlTagScanner.ReadElementName(text, openStart);
        return name is not null && line[2..].StartsWith(name) && line[(2 + name.Length)..].TrimStart().StartsWith(">");
    }

    /// <summary>
    /// An attribute on a line of its own: under the first attribute when that's on the tag's line
    /// ("&lt;Button Width=..." puts the next one under "Width"), otherwise one level inside the tag.
    /// </summary>
    private static string AttributeIndent(string text, int tagStart, string indentUnit)
    {
        var tagLineStart = LineStartOf(text, tagStart);
        var tagIndent = IndentOfLine(text, tagLineStart);

        var i = tagStart + 1;
        while (i < text.Length && XamlTagScanner.IsNameChar(text[i]))
            i++;
        var afterName = i;
        while (i < text.Length && text[i] is ' ' or '\t')
            i++;
        if (i == afterName || i >= text.Length || text[i] is '\r' or '\n')
            return tagIndent + indentUnit;

        // Keep the tag line's own indentation characters, then pad with spaces up to the attribute's column
        return tagIndent + new string(' ', i - tagLineStart - tagIndent.Length);
    }

    public static string IndentOfLine(string text, int lineStart)
    {
        var end = lineStart;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[lineStart..end];
    }

    private static int LineStartOf(string text, int offset)
        => offset <= 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    private static int LineEnd(string text, int lineStart)
    {
        var end = text.IndexOf('\n', lineStart);
        return end < 0 ? text.Length : end;
    }
}
