using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Services.CSharp;

namespace AXamlIDE.Services.Commenting;

/// <summary>
/// Toggle comment for AXAML (and any XML), working on whole elements, since XML can't comment out half a tag:
///
///  - The caret (or selection) is inside a comment: Toggle takes that comment's <c>&lt;!--</c> and <c>--&gt;</c>
///    off, so the whole commented block comes back, wherever in it the caret is.
///  - Otherwise the range grows to whole elements. The caret on <c>&lt;StackPanel</c>, on one of its attributes or
///    on <c>&lt;/StackPanel&gt;</c> means the whole StackPanel, children and all; a caret on a child's line means
///    just that child. A selection over several children takes all of them, each from its start to its end.
///  - XML comments can't nest (a comment can't contain "--"), so comments already inside the range are left as
///    they are, and the text around them is commented in pieces: <c>&lt;!--A--&gt;&lt;!-- old --&gt;&lt;!--B--&gt;</c>.
///
/// Markers go in without spaces, as Visual Studio does: <c>&lt;!--&lt;TextBlock /&gt;--&gt;</c>.
/// </summary>
public static class XmlCommenter
{
    private const string Open = "<!--";
    private const string Close = "-->";

    /// <summary>
    /// The edits for the caret or selection (<paramref name="selectionStart"/> equals <paramref name="selectionEnd"/>
    /// when nothing is selected). Listed from the end of the text, so they can be applied in order. Empty when there's
    /// nothing to do (a blank line, or Uncomment with no comment there).
    /// </summary>
    public static IReadOnlyList<TextEdit> Apply(string text, int selectionStart, int selectionEnd, CommentMode mode)
    {
        if (TargetRange(text, selectionStart, selectionEnd) is not var (start, end))
            return [];

        var scan = XmlScan.Scan(text);

        var uncomment = mode switch
        {
            CommentMode.Comment => false,
            CommentMode.Uncomment => true,
            _ => IsAllComment(text, start, end, scan.Comments)
        };
        if (uncomment)
            return Uncomment(text, scan.Comments.Where(c => c.Start < end && start < c.End));

        if (IsAllComment(text, start, end, scan.Comments))
            return [];   // Comment on something that's already a comment: XML can't nest them
        (start, end) = GrowToWholeElements(start, end, scan);
        return CommentAround(text, start, end, scan.Comments);
    }

    /// <summary>
    /// The text the command is about, without the whitespace around it: the selection, or with no selection the
    /// caret's line. Null when that's all whitespace.
    /// </summary>
    private static (int Start, int End)? TargetRange(string text, int selectionStart, int selectionEnd)
    {
        if (selectionEnd < selectionStart)
            (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var end = Math.Clamp(selectionEnd, 0, text.Length);
        if (start == end)
        {
            start = LineCommenter.LineStart(text, start);
            end = LineCommenter.LineEnd(text, start);
        }

        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        return start < end ? (start, end) : null;
    }

    /// <summary>True when every non-whitespace character between start and end is inside a comment.</summary>
    private static bool IsAllComment(string text, int start, int end, IReadOnlyList<XmlComment> comments)
    {
        var i = start;
        while (i < end)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var comment = comments.FirstOrDefault(c => c.Start <= i && i < c.End);
            if (comment is null)
                return false;
            i = comment.End;
        }
        return true;
    }

    /// <summary>
    /// Grow the range until no element or comment is cut in half. An element that overlaps the range is taken
    /// whole, unless the range sits entirely inside its content (between its start and end tags): that's the
    /// parent of what's selected, not part of it.
    /// </summary>
    private static (int Start, int End) GrowToWholeElements(int start, int end, XmlScan scan)
    {
        bool grew;
        do
        {
            grew = false;
            foreach (var element in scan.Elements)
            {
                var overlaps = element.Start < end && start < element.End;
                var insideContent = element.ContentStart <= start && end <= element.ContentEnd;
                if (!overlaps || insideContent)
                    continue;
                if (element.Start < start) { start = element.Start; grew = true; }
                if (element.End > end) { end = element.End; grew = true; }
            }
            foreach (var comment in scan.Comments)
            {
                if (comment.Start >= end || start >= comment.End)
                    continue;
                if (comment.Start < start) { start = comment.Start; grew = true; }
                if (comment.End > end) { end = comment.End; grew = true; }
            }
        } while (grew);
        return (start, end);
    }

    /// <summary>
    /// Wrap start..end in a comment. Comments already inside it stay as they are, and each stretch between them that
    /// has more than whitespace gets its own markers.
    /// </summary>
    private static IReadOnlyList<TextEdit> CommentAround(string text, int start, int end, IReadOnlyList<XmlComment> comments)
    {
        var edits = new List<TextEdit>();
        var pieceStart = start;
        foreach (var comment in comments.Where(c => start <= c.Start && c.End <= end).OrderBy(c => c.Start))
        {
            AddPiece(pieceStart, comment.Start);
            pieceStart = comment.End;
        }
        AddPiece(pieceStart, end);

        edits.Reverse();   // the last piece first, so earlier offsets stay valid
        return edits;

        void AddPiece(int from, int to)
        {
            while (from < to && char.IsWhiteSpace(text[from]))
                from++;
            while (to > from && char.IsWhiteSpace(text[to - 1]))
                to--;
            if (from == to)
                return;
            edits.Add(new TextEdit(from, 0, Open));
            edits.Add(new TextEdit(to, 0, Close));
        }
    }

    /// <summary>
    /// Take the markers off each comment. A space just inside both markers goes too (<c>&lt;!-- x --&gt;</c> becomes
    /// <c>x</c>). A comment with no <c>--&gt;</c> (it runs to the end of the file) only loses its <c>&lt;!--</c>.
    /// </summary>
    private static IReadOnlyList<TextEdit> Uncomment(string text, IEnumerable<XmlComment> comments)
    {
        var edits = new List<TextEdit>();
        foreach (var comment in comments.OrderByDescending(c => c.Start))
        {
            var contentStart = comment.Start + Open.Length;
            var contentEnd = comment.IsClosed ? comment.End - Close.Length : comment.End;
            var padded = comment.IsClosed
                         && contentEnd - contentStart >= 2
                         && text[contentStart] == ' '
                         && text[contentEnd - 1] == ' ';

            if (comment.IsClosed)
                edits.Add(padded
                    ? new TextEdit(contentEnd - 1, Close.Length + 1, "")
                    : new TextEdit(contentEnd, Close.Length, ""));
            edits.Add(padded
                ? new TextEdit(comment.Start, Open.Length + 1, "")
                : new TextEdit(comment.Start, Open.Length, ""));
        }
        return edits;
    }
}

/// <summary>One <c>&lt;!-- ... --&gt;</c>, from its '&lt;' to just after its '&gt;'.</summary>
internal sealed record XmlComment(int Start, int End, bool IsClosed);

/// <summary>
/// One element, from its start tag's '&lt;' to just after its end tag's '&gt;' (or the start tag's "/&gt;").
/// Its content is what's between the two tags; empty for <c>&lt;X /&gt;</c>.
/// </summary>
internal sealed record XmlElement(string Name, int Start, int ContentStart, int ContentEnd, int End);

/// <summary>
/// A forgiving pass over XML text that finds the comments and the elements. It never fails on a half-typed
/// document: a tag with no end tag counts as just its start tag, and an end tag with no start tag is ignored.
/// Quoted attribute values, CDATA and &lt;? ... ?&gt; are stepped over, so a '&lt;' or "&lt;!--" inside them
/// doesn't count.
/// </summary>
internal sealed class XmlScan
{
    public List<XmlComment> Comments { get; } = [];
    public List<XmlElement> Elements { get; } = [];

    public static XmlScan Scan(string text)
    {
        var scan = new XmlScan();
        var open = new Stack<(string Name, int Start, int TagEnd)>();   // start tags still waiting for their end tag
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (At(text, i, "<!--"))
            {
                var close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 3;
                scan.Comments.Add(new XmlComment(i, end, close >= 0));
                i = end;
            }
            else if (At(text, i, "<![CDATA["))
            {
                i = SkipPast(text, i, "]]>");
            }
            else if (At(text, i, "<?"))
            {
                i = SkipPast(text, i, "?>");
            }
            else if (At(text, i, "<!"))
            {
                i = SkipPast(text, i, ">");   // <!DOCTYPE ...>
            }
            else if (i + 1 < text.Length && text[i + 1] == '/')
            {
                // </Name>: close the nearest open element with that name (anything opened after it was never closed).
                var name = ReadName(text, i + 2);
                var end = TagEnd(text, i + 2 + name.Length);
                if (open.Any(o => o.Name == name))
                {
                    var element = open.Pop();
                    while (element.Name != name)
                        element = open.Pop();
                    scan.Elements.Add(new XmlElement(name, element.Start, element.TagEnd, i, end));
                }
                i = end;
            }
            else if (i + 1 < text.Length && IsNameChar(text[i + 1]))
            {
                var name = ReadName(text, i + 1);
                var end = TagEnd(text, i + 1 + name.Length);
                var selfClosing = end >= 2 && text[end - 1] == '>' && text[end - 2] == '/';
                if (selfClosing)
                    scan.Elements.Add(new XmlElement(name, i, end, end, end));
                else
                    open.Push((name, i, end));
                i = end;
            }
            else
            {
                i++;
            }
        }

        foreach (var (name, start, tagEnd) in open)   // never closed: just the start tag
            scan.Elements.Add(new XmlElement(name, start, tagEnd, tagEnd, tagEnd));
        return scan;
    }

    private static bool At(string text, int offset, string value)
        => string.CompareOrdinal(text, offset, value, 0, value.Length) == 0;

    private static int SkipPast(string text, int offset, string terminator)
    {
        var found = text.IndexOf(terminator, offset, StringComparison.Ordinal);
        return found < 0 ? text.Length : found + terminator.Length;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    private static string ReadName(string text, int offset)
    {
        var end = offset;
        while (end < text.Length && IsNameChar(text[end]))
            end++;
        return text[offset..end];
    }

    /// <summary>
    /// Just after the '&gt;' that ends a tag, stepping over quoted attribute values. A tag that's still being typed
    /// (a '&lt;' turns up first, or the text ends) ends where it stops.
    /// </summary>
    private static int TagEnd(string text, int offset)
    {
        var i = offset;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                var closeQuote = text.IndexOf(c, i + 1);
                if (closeQuote < 0)
                    return text.Length;
                i = closeQuote + 1;
            }
            else if (c == '>')
                return i + 1;
            else if (c == '<')
                return i;
            else
                i++;
        }
        return text.Length;
    }
}
