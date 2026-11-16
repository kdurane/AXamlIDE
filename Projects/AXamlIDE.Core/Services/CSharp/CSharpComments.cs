using System;

namespace AXamlIDE.Services.CSharp;

/// <summary>What kind of comment a position in code is in.</summary>
public enum CommentKind
{
    None,

    /// <summary><c>// ...</c> or <c>/* ... */</c> (in AXAML, <c>&lt;!-- ... --&gt;</c>).</summary>
    Plain,

    /// <summary><c>/// ...</c>: a documentation comment, where '&lt;' starts a tag such as <c>&lt;see&gt;</c>.</summary>
    Doc
}

/// <summary>
/// Finds whether a position in C# is inside a comment, so typing there doesn't open completion. It reads the text
/// from the start, skipping strings (regular, verbatim, raw and character literals) so a "//" in one isn't taken for a
/// comment. Interpolation holes are read as part of their string, which is only wrong for a string with quotes
/// inside a hole (<c>$"{d["key"]}"</c>).
/// </summary>
public static class CSharpComments
{
    /// <summary>The comment <paramref name="offset"/> (a caret position: between two characters) is in.</summary>
    public static CommentKind KindAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var i = 0;
        while (i < offset)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                var lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0 || lineEnd >= offset)
                    return IsDocComment(text, i) ? CommentKind.Doc : CommentKind.Plain;
                i = lineEnd + 1;
            }
            else if (c == '/' && next == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0 || close + 2 > offset)
                    return CommentKind.Plain;   // the caret is before the "*/" (or there isn't one yet)
                i = close + 2;
            }
            else if (c == '\'')
                i = SkipQuoted(text, i + 1, '\'');
            else if (c is '"' or '@' or '$')
                i = SkipString(text, i);
            else
                i++;
        }

        return CommentKind.None;
    }

    /// <summary>"///" but not "////" (four or more slashes is a plain comment, as in Roslyn).</summary>
    private static bool IsDocComment(string text, int slashes)
        => slashes + 2 < text.Length && text[slashes + 2] == '/'
           && (slashes + 3 >= text.Length || text[slashes + 3] != '/');

    /// <summary>
    /// From a '"', '@' or '$' at <paramref name="start"/>: past the string it starts, or one character on when it
    /// doesn't start one (a verbatim name like <c>@class</c>, or '$' on its own).
    /// </summary>
    private static int SkipString(string text, int start)
    {
        var i = start;
        var verbatim = false;
        while (i < text.Length && text[i] is '@' or '$')
        {
            verbatim |= text[i] == '@';
            i++;
        }

        if (i >= text.Length || text[i] != '"')
            return start + 1;

        var quotes = 0;
        while (i + quotes < text.Length && text[i + quotes] == '"')
            quotes++;

        if (quotes >= 3)   // raw string: ends at the same number of quotes
        {
            var close = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + quotes;
        }

        if (quotes == 2)
            return i + 2;   // ""

        return verbatim ? SkipVerbatim(text, i + 1) : SkipQuoted(text, i + 1, '"');
    }

    /// <summary>Past the closing <paramref name="quote"/>, skipping '\' escapes; a string can't run past its line.</summary>
    private static int SkipQuoted(string text, int i, char quote)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
                i += 2;
            else if (c == quote)
                return i + 1;
            else if (c == '\n')
                return i;   // unclosed: still being typed
            else
                i++;
        }

        return text.Length;
    }

    /// <summary>Past the closing '"' of @"...", where "" is a quote and lines can break.</summary>
    private static int SkipVerbatim(string text, int i)
    {
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }
}
