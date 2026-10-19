using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AXamlIDE.Services.CSharp;

/// <summary>One replacement in a text (offsets into the original).</summary>
public sealed record TextEdit(int Offset, int Length, string Replacement);

/// <summary>
/// Typing ';' after <c>namespace X</c> means "make it file-scoped": remove the namespace's '{' and its matching
/// last '}', and take one level of indentation off what was inside. Only when that's unambiguous: the next thing
/// after the ';' is the '{', and its matching '}' is the last thing in the file.
/// </summary>
public static partial class FileScopedNamespace
{
    [GeneratedRegex(@"^[ \t]*namespace[ \t]+@?[A-Za-z_][\w.@]*[ \t]*;$")]
    private static partial Regex NamespaceLine();

    /// <summary>
    /// The edits that make the namespace file-scoped, given the offset of the ';' just typed; null when it doesn't
    /// apply. The edits don't overlap and are in order from the end of the text, so they can be applied as listed.
    /// </summary>
    public static IReadOnlyList<TextEdit>? ConvertAfterSemicolon(string text, int semicolon)
    {
        if (semicolon < 0 || semicolon >= text.Length || text[semicolon] != ';')
            return null;
        var lineStart = text.LastIndexOf('\n', Math.Max(semicolon - 1, 0)) + 1;
        if (semicolon > 0 && text[semicolon - 1] == '\n')
            lineStart = semicolon;
        if (!NamespaceLine().IsMatch(text[lineStart..(semicolon + 1)]))
            return null;

        // The next thing must be the namespace's '{', and its '}' the last thing in the file.
        var open = SkipWhitespace(text, semicolon + 1);
        if (open >= text.Length || text[open] != '{')
            return null;
        if (MatchingBrace(text, open) is not { } close || SkipWhitespace(text, close + 1) != text.Length)
            return null;

        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var bodyStart = LineEnd(text, open) is var openLineEnd && IsBlank(text, open + 1, openLineEnd) ? AfterNewline(text, openLineEnd) : open + 1;
        var closeLineStart = text.LastIndexOf('\n', close - 1) + 1;
        var closeOnOwnLine = IsBlank(text, closeLineStart, close) && IsBlank(text, close + 1, LineEnd(text, close));
        var bodyEnd = closeOnOwnLine ? closeLineStart : close;

        var edits = new List<TextEdit>();

        // The '}' (and its line, if it's alone there), up to the end of the file.
        var removeFrom = closeOnOwnLine ? closeLineStart : close;
        edits.Add(new TextEdit(removeFrom, text.Length - removeFrom, closeOnOwnLine ? "" : newline));

        // One level of indentation off the body, when every line has it.
        var lines = BodyLines(text, bodyStart, bodyEnd);
        if (IndentUnit(text, lines) is { } unit)
            foreach (var (start, _) in lines.AsEnumerable().Reverse())
                if (!IsBlank(text, start, LineEnd(text, start)))
                    edits.Add(new TextEdit(start, unit.Length, ""));

        // The '{' (and blank lines after it): one empty line after the namespace, as Visual Studio leaves.
        var firstCode = bodyStart;
        while (firstCode < bodyEnd && IsBlank(text, firstCode, LineEnd(text, firstCode)) && LineEnd(text, firstCode) < bodyEnd)
            firstCode = AfterNewline(text, LineEnd(text, firstCode));
        edits.Add(new TextEdit(semicolon + 1, firstCode - (semicolon + 1), newline + newline));

        return edits;
    }

    /// <summary>The '}' matching the '{' at <paramref name="open"/>, skipping strings, characters and comments; null if none.</summary>
    public static int? MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                        return i;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    i = LineEnd(text, i) - 1;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 1;
                    break;
                case '"':
                    i = SkipString(text, i);
                    break;
                case '\'':
                    i = SkipCharacter(text, i);
                    break;
            }
        }
        return null;
    }

    /// <summary>The last offset of the string literal starting at <paramref name="quote"/> (normal, verbatim @"", or raw """).</summary>
    private static int SkipString(string text, int quote)
    {
        // @"..." (or $@"/@$"): "" is a quote. Checked first: @""" is a verbatim string, not a raw one.
        var verbatim = quote > 0 && text[quote - 1] == '@' || quote > 1 && text[quote - 1] == '$' && text[quote - 2] == '@';
        if (!verbatim && string.CompareOrdinal(text, quote, "\"\"\"", 0, 3) == 0)
        {
            var end = text.IndexOf("\"\"\"", quote + 3, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + 2;
        }

        for (var i = quote + 1; i < text.Length; i++)
        {
            if (verbatim)
            {
                if (text[i] == '"' && (i + 1 >= text.Length || text[i + 1] != '"'))
                    return i;
                if (text[i] == '"')
                    i++;   // "" inside a verbatim string
            }
            else if (text[i] == '\\')
                i++;
            else if (text[i] is '"' or '\n')
                return i;
        }
        return text.Length;
    }

    private static int SkipCharacter(string text, int quote)
    {
        for (var i = quote + 1; i < text.Length && i < quote + 8; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] is '\'' or '\n')
                return i;
        }
        return quote;   // not a character literal after all
    }

    /// <summary>The body's lines: (start offset, end offset without the newline).</summary>
    private static List<(int Start, int End)> BodyLines(string text, int from, int to)
    {
        var lines = new List<(int, int)>();
        for (var start = from; start < to; start = AfterNewline(text, LineEnd(text, start)))
            lines.Add((start, Math.Min(LineEnd(text, start), to)));
        return lines;
    }

    /// <summary>The indentation every non-blank body line starts with (a tab, or the first line's spaces up to 4); null if not all do.</summary>
    private static string? IndentUnit(string text, List<(int Start, int End)> lines)
    {
        var code = lines.Where(line => !IsBlank(text, line.Start, line.End)).ToList();
        if (code.Count == 0)
            return null;
        var first = text[code[0].Start..code[0].End];
        var unit = first.StartsWith('\t') ? "\t" : new string(' ', Math.Min(4, first.Length - first.TrimStart(' ').Length));
        if (unit.Length == 0)
            return null;
        return code.All(line => string.CompareOrdinal(text, line.Start, unit, 0, unit.Length) == 0) ? unit : null;
    }

    private static int SkipWhitespace(string text, int from)
    {
        while (from < text.Length && char.IsWhiteSpace(text[from]))
            from++;
        return from;
    }

    private static int LineEnd(string text, int from)
    {
        var end = text.IndexOf('\n', from);
        if (end < 0)
            return text.Length;
        return end > 0 && text[end - 1] == '\r' ? end - 1 : end;
    }

    private static int AfterNewline(string text, int lineEnd)
        => lineEnd < text.Length && text[lineEnd] == '\r' ? Math.Min(lineEnd + 2, text.Length) : Math.Min(lineEnd + 1, text.Length);

    private static bool IsBlank(string text, int from, int to)
    {
        for (var i = from; i < to && i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i]))
                return false;
        return true;
    }
}
