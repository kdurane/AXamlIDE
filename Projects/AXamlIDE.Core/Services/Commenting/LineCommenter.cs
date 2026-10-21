using System;
using System.Collections.Generic;
using AXamlIDE.Services.CSharp;

namespace AXamlIDE.Services.Commenting;

/// <summary>What Ctrl+/ (toggle), Ctrl+K, Ctrl+C (comment) and Ctrl+K, Ctrl+U (uncomment) ask for.</summary>
public enum CommentMode
{
    Toggle,
    Comment,
    Uncomment
}

/// <summary>
/// Line comments (C#'s <c>//</c>), the way VS Code and Rider do them: every line the selection touches (or the
/// caret's line) gets <c>// </c> at the smallest indent of those lines, so the markers line up. Toggle removes
/// them instead when every non-blank line already starts with the marker. Blank lines are left alone.
/// </summary>
public static class LineCommenter
{
    /// <summary>
    /// The edits for the lines between <paramref name="selectionStart"/> and <paramref name="selectionEnd"/>
    /// (equal when nothing is selected). Listed from the end of the text, so they can be applied in order.
    /// </summary>
    public static IReadOnlyList<TextEdit> Apply(string text, int selectionStart, int selectionEnd, CommentMode mode, string marker = "//")
    {
        var lines = LinesIn(text, selectionStart, selectionEnd);

        // Where each non-blank line's text starts (after its indent), and the smallest indent of them all.
        var smallestIndent = int.MaxValue;
        var allCommented = true;
        var anyNonBlank = false;
        foreach (var (start, end) in lines)
        {
            var indent = IndentLength(text, start, end);
            if (start + indent == end)
                continue;   // blank line
            anyNonBlank = true;
            smallestIndent = Math.Min(smallestIndent, indent);
            if (!StartsWithAt(text, start + indent, marker))
                allCommented = false;
        }
        if (!anyNonBlank)
            return [];

        var uncomment = mode switch
        {
            CommentMode.Comment => false,
            CommentMode.Uncomment => true,
            _ => allCommented
        };

        var edits = new List<TextEdit>();
        for (var i = lines.Count - 1; i >= 0; i--)   // from the last line up, so earlier offsets stay valid
        {
            var (start, end) = lines[i];
            var indent = IndentLength(text, start, end);
            var textStart = start + indent;
            if (textStart == end)
                continue;

            if (!uncomment)
            {
                edits.Add(new TextEdit(start + smallestIndent, 0, marker + " "));
            }
            else if (StartsWithAt(text, textStart, marker))
            {
                // Take the marker off, and the one space after it if there is one ("// x" -> "x").
                var length = marker.Length;
                if (textStart + length < end && text[textStart + length] == ' ')
                    length++;
                edits.Add(new TextEdit(textStart, length, ""));
            }
        }
        return edits;
    }

    /// <summary>
    /// The (start, end) of every line the selection touches, without line breaks. A selection that ends at the very
    /// start of a line (a whole-line selection made with Shift+Down) doesn't include that last line.
    /// </summary>
    internal static List<(int Start, int End)> LinesIn(string text, int selectionStart, int selectionEnd)
    {
        if (selectionEnd < selectionStart)
            (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionEnd = Math.Clamp(selectionEnd, 0, text.Length);
        if (selectionEnd > selectionStart && LineStart(text, selectionEnd) == selectionEnd)
            selectionEnd--;   // step back onto the previous line's line break

        var lines = new List<(int, int)>();
        var start = LineStart(text, selectionStart);
        while (true)
        {
            var end = LineEnd(text, start);
            lines.Add((start, end));
            var next = text.IndexOf('\n', start);
            if (next < 0 || next >= selectionEnd)
                break;
            start = next + 1;
        }
        return lines;
    }

    /// <summary>The offset where the line holding <paramref name="offset"/> starts.</summary>
    internal static int LineStart(string text, int offset)
        => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>The offset where the line starting at <paramref name="lineStart"/> ends, before any "\r\n" or "\n".</summary>
    internal static int LineEnd(string text, int lineStart)
    {
        var end = text.IndexOf('\n', lineStart);
        if (end < 0)
            return text.Length;
        return end > lineStart && text[end - 1] == '\r' ? end - 1 : end;
    }

    private static int IndentLength(string text, int start, int end)
    {
        var i = start;
        while (i < end && (text[i] == ' ' || text[i] == '\t'))
            i++;
        return i - start;
    }

    private static bool StartsWithAt(string text, int offset, string value)
        => string.CompareOrdinal(text, offset, value, 0, value.Length) == 0;
}
