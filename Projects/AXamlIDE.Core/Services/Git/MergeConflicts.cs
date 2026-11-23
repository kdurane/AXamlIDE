using System;
using System.Collections.Generic;
using System.Linq;

namespace AXamlIDE.Services.Git;

/// <summary>Which side of a conflict to keep (VS Code's Accept Current / Incoming / Both Changes).</summary>
public enum ConflictChoice
{
    Current,
    Incoming,
    Both
}

/// <summary>
/// One conflict git wrote into a file: the 1-based lines of its markers. "&lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD" starts
/// the current side (ours), "|||||||" (diff3 style only) the common base, "=======" the incoming side (theirs),
/// and "&gt;&gt;&gt;&gt;&gt;&gt;&gt; branch" ends it.
/// </summary>
public readonly record struct MergeConflict(int StartLine, int? BaseLine, int SeparatorLine, int EndLine)
{
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>Finds conflict markers in a text and resolves them, as VS Code's inline merge actions do.</summary>
public static class MergeConflicts
{
    public static IReadOnlyList<MergeConflict> Find(string text)
    {
        var conflicts = new List<MergeConflict>();
        if (!text.Contains("<<<<<<<", StringComparison.Ordinal))
            return conflicts;

        var lines = TextLines.Of(text);
        int? start = null, baseLine = null, separator = null;
        for (var line = 1; line <= lines.Count; line++)
        {
            var content = text.AsSpan(lines.Start(line), lines.End(line) - lines.Start(line));
            if (IsMarker(content, '<'))
                (start, baseLine, separator) = (line, null, null);   // a new start drops an unfinished one
            else if (start is not null && separator is null && IsMarker(content, '|'))
                baseLine = line;
            else if (start is not null && separator is null && content.SequenceEqual("======="))
                separator = line;
            else if (start is not null && separator is not null && IsMarker(content, '>'))
            {
                conflicts.Add(new MergeConflict(start.Value, baseLine, separator.Value, line));
                (start, baseLine, separator) = (null, null, null);
            }
        }
        return conflicts;
    }

    /// <summary>Seven of the character, then the end of the line or a space ("&lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD").</summary>
    private static bool IsMarker(ReadOnlySpan<char> line, char marker)
        => line.Length >= 7 && line[..7].IndexOfAnyExcept(marker) < 0 && (line.Length == 7 || line[7] == ' ');

    /// <summary>The edit that replaces the whole conflict, markers and all, with the side(s) kept.</summary>
    public static TextEdit Resolve(string text, MergeConflict conflict, ConflictChoice choice)
    {
        var lines = TextLines.Of(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var current = Lines(text, lines, conflict.StartLine + 1, (conflict.BaseLine ?? conflict.SeparatorLine) - 1);
        var incoming = Lines(text, lines, conflict.SeparatorLine + 1, conflict.EndLine - 1);
        var kept = choice switch
        {
            ConflictChoice.Current => current,
            ConflictChoice.Incoming => incoming,
            _ => [.. current, .. incoming]
        };

        var start = lines.Start(conflict.StartLine);
        // Up to the next line's start, so the end marker's line ending goes too (at the end of the text, its end).
        var atEnd = conflict.EndLine >= lines.Count;
        var end = atEnd ? text.Length : lines.Start(conflict.EndLine + 1);
        var replacement = kept.Count == 0 ? "" : string.Join(newline, kept) + (atEnd ? "" : newline);
        return new TextEdit(start, end - start, replacement);
    }

    private static List<string> Lines(string text, TextLines lines, int first, int last)
        => Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Select(line => text[lines.Start(line)..lines.End(line)])
            .ToList();
}
