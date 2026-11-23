using System.Collections.Generic;
using DiffPlex;

namespace AXamlIDE.Services.Git;

public enum LineChangeKind
{
    Added,      // new lines
    Modified,   // lines that replaced others
    Deleted     // lines removed from between two lines (nothing to colour, so a marker between them)
}

/// <summary>
/// A block of changed lines in the current text. <see cref="FirstLine"/> is 1-based. For
/// <see cref="LineChangeKind.Deleted"/>, <see cref="LineCount"/> is 0 and the lines were removed just below
/// <see cref="FirstLine"/> (0: above the first line). <see cref="BaseIndex"/> (0-based) and
/// <see cref="BaseCount"/> are the lines it replaced in the base text (none for Added), to show or put back.
/// </summary>
public readonly record struct LineChange(int FirstLine, int LineCount, LineChangeKind Kind, int BaseIndex = 0, int BaseCount = 0)
{
    public int LastLine => FirstLine + LineCount - 1;

    /// <summary>Whether the 1-based <paramref name="line"/> is in it (a deletion: the line it's below, or line 1 for 0).</summary>
    public bool Contains(int line) => Kind == LineChangeKind.Deleted
        ? line == System.Math.Max(1, FirstLine)
        : line >= FirstLine && line <= LastLine;
}

/// <summary>Replace <see cref="Length"/> characters at <see cref="Offset"/> with <see cref="Text"/>.</summary>
public readonly record struct TextEdit(int Offset, int Length, string Text);

/// <summary>
/// How a file's text differs from its committed (or staged) text, line by line: what the editor's gutter change
/// bars show (VS Code's "quick diff", Visual Studio's and Rider's change markers). Whitespace changes count.
/// </summary>
public static class LineChanges
{
    private static readonly Differ Differ = new();

    public static IReadOnlyList<LineChange> Compare(string baseText, string currentText)
    {
        var changes = new List<LineChange>();
        foreach (var block in Differ.CreateLineDiffs(baseText, currentText, ignoreWhitespace: false).DiffBlocks)
        {
            if (block.InsertCountB == 0)
                changes.Add(new LineChange(block.InsertStartB, 0, LineChangeKind.Deleted, block.DeleteStartA, block.DeleteCountA));
            else
                changes.Add(new LineChange(block.InsertStartB + 1, block.InsertCountB,
                    block.DeleteCountA == 0 ? LineChangeKind.Added : LineChangeKind.Modified, block.DeleteStartA, block.DeleteCountA));
        }
        return changes;
    }

    /// <summary>The base text's lines a change replaced (the base split on '\n', as <see cref="Compare"/> got it).</summary>
    public static IReadOnlyList<string> BaseLines(string baseText, LineChange change)
    {
        var lines = baseText.Split('\n');
        var count = System.Math.Max(0, System.Math.Min(change.BaseCount, lines.Length - change.BaseIndex));
        return change.BaseIndex >= lines.Length ? [] : lines[change.BaseIndex..(change.BaseIndex + count)];
    }

    /// <summary>
    /// The edit to <paramref name="text"/> that puts one change back as it was (VS Code's Revert Change, Rider's
    /// Rollback Lines): <paramref name="baseLines"/> in place of the changed lines, joined with the text's own line
    /// ending.
    /// </summary>
    public static TextEdit Revert(string text, LineChange change, IReadOnlyList<string> baseLines)
    {
        var newline = text.Contains("\r\n", System.StringComparison.Ordinal) ? "\r\n" : "\n";
        var restored = string.Join(newline, baseLines);
        var lines = TextLines.Of(text);

        if (change.Kind == LineChangeKind.Deleted)
            return change.FirstLine <= 0
                ? new TextEdit(0, 0, restored + newline)
                : new TextEdit(lines.End(change.FirstLine), 0, newline + restored);

        var start = lines.Start(change.FirstLine);
        if (change.Kind == LineChangeKind.Modified)
            return new TextEdit(start, lines.End(change.LastLine) - start, restored);

        // Added: take the lines out with their line endings; at the end of the text, the one before them instead.
        if (change.LastLine < lines.Count)
            return new TextEdit(start, lines.Start(change.LastLine + 1) - start, "");
        var from = change.FirstLine > 1 ? lines.End(change.FirstLine - 1) : 0;
        return new TextEdit(from, text.Length - from, "");
    }
}

/// <summary>Where each line of a text starts and ends (without its line ending); lines are 1-based.</summary>
public sealed class TextLines
{
    private readonly List<(int Start, int End)> _lines = [];

    private TextLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            _lines.Add((start, i > start && text[i - 1] == '\r' ? i - 1 : i));
            start = i + 1;
        }
        _lines.Add((start, text.Length));
    }

    public static TextLines Of(string text) => new(text);

    public int Count => _lines.Count;
    public int Start(int line) => _lines[line - 1].Start;
    public int End(int line) => _lines[line - 1].End;
}
