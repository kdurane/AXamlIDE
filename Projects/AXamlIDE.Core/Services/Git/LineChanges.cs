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
/// <see cref="FirstLine"/> (0: above the first line).
/// </summary>
public readonly record struct LineChange(int FirstLine, int LineCount, LineChangeKind Kind)
{
    public int LastLine => FirstLine + LineCount - 1;
}

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
                changes.Add(new LineChange(block.InsertStartB, 0, LineChangeKind.Deleted));
            else
                changes.Add(new LineChange(block.InsertStartB + 1, block.InsertCountB,
                    block.DeleteCountA == 0 ? LineChangeKind.Added : LineChangeKind.Modified));
        }
        return changes;
    }
}
