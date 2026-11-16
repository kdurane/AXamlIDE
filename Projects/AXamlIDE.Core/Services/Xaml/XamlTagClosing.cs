using System;
using System.Collections.Generic;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// What auto-closing tags needs to know about the text after the caret:
///   - typing '&gt;' adds &lt;/Grid&gt; only when the element hasn't already got one (the '&gt;' was deleted and is
///     being typed again: its content and end tag are still there);
///   - typing '/' before the '&gt;' of an empty element (&lt;Grid&gt;&lt;/Grid&gt;) makes it self-closing, so its end
///     tag goes.
/// </summary>
public static class XamlTagClosing
{
    /// <summary>
    /// Whether the element whose start tag ends just before <paramref name="from"/> is closed later in the text. The
    /// elements after it are matched up in pairs; the first end tag left over is either this one's (closed) or its
    /// parent's (not closed). When the parent has the same name (a Grid in a Grid) the two can't be told apart by
    /// nesting, so the end tag counts as this one's only if it's on the same line or lined up under it.
    /// </summary>
    public static bool HasEndTag(string text, int from, string tagName)
    {
        var tagStart = from > 0 ? text.LastIndexOf('<', from - 1) : -1;
        if (tagStart < 0)
            return false;
        var open = new Stack<string>();
        var i = from;
        while ((i = text.IndexOf('<', i)) >= 0)
        {
            if (Skip(text, i, "<!--", "-->") is { } afterComment) { i = afterComment; continue; }
            if (Skip(text, i, "<![CDATA[", "]]>") is { } afterData) { i = afterData; continue; }
            if (Skip(text, i, "<?", "?>") is { } afterInstruction) { i = afterInstruction; continue; }

            var isEnd = i + 1 < text.Length && text[i + 1] == '/';
            var nameStart = isEnd ? i + 2 : i + 1;
            var nameEnd = nameStart;
            while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
                nameEnd++;
            if (nameEnd == nameStart)
            {
                i++;   // a stray '<'
                continue;
            }
            var name = text[nameStart..nameEnd];
            var tagEnd = TagEnd(text, nameEnd);

            if (isEnd)
            {
                if (open.Count == 0)   // left over: this element's end tag, or its parent's
                    return name == tagName
                           && (LineStart(text, i) == LineStart(text, tagStart) || Indent(text, i) == Indent(text, tagStart));
                // Pop to the matching start tag; one that never matches (a typo) is ignored.
                if (open.Contains(name))
                    while (open.Pop() != name) { }
            }
            else if (tagEnd < 0 || text[tagEnd - 1] != '/')
            {
                open.Push(name);
            }

            i = tagEnd < 0 ? nameEnd : tagEnd + 1;   // unfinished: carry on from the next tag
        }
        return false;
    }

    /// <summary>
    /// With the start tag's '&gt;' at <paramref name="gt"/>: the range from just after it to the end of an end tag for
    /// <paramref name="tagName"/> that follows with nothing but whitespace between, or null when the element isn't empty.
    /// </summary>
    public static (int Start, int Length)? EmptyEndTagAfter(string text, int gt, string tagName)
    {
        if (gt >= text.Length || text[gt] != '>')
            return null;
        var i = gt + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        var endTag = $"</{tagName}";
        if (string.CompareOrdinal(text, i, endTag, 0, endTag.Length) != 0)
            return null;
        i += endTag.Length;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        return i < text.Length && text[i] == '>' ? (gt + 1, i + 1 - (gt + 1)) : null;
    }

    /// <summary>
    /// Where the tag whose name ends at <paramref name="from"/> ends ('&gt;' outside quotes), or -1 when another tag
    /// starts first (this one is unfinished) or the text ends.
    /// </summary>
    private static int TagEnd(string text, int from)
    {
        char? quote = null;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '>')
                return i;
            else if (c == '<')
                return -1;   // a tag left unfinished: stop here rather than misread the rest
        }
        return -1;
    }

    private static int LineStart(string text, int at) => at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;

    /// <summary>The width of the whitespace that starts the line holding <paramref name="at"/>.</summary>
    private static string Indent(string text, int at)
    {
        var start = LineStart(text, at);
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[start..end];
    }

    private static int? Skip(string text, int at, string start, string end)
    {
        if (string.CompareOrdinal(text, at, start, 0, start.Length) != 0)
            return null;
        var close = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + end.Length;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';
}
