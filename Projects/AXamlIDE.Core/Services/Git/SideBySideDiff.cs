using System.Collections.Generic;
using System.Linq;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace AXamlIDE.Services.Git;

public enum DiffLineKind
{
    Unchanged,
    Removed,    // only on the left
    Added,      // only on the right
    Modified,   // changed: the old line on the left, the new one on the right
    Filler      // a blank line that keeps the two sides lined up
}

/// <param name="Number">The line's number in its file (null for filler lines).</param>
public sealed record DiffLine(int? Number, string Text, DiffLineKind Kind);

/// <summary>
/// Two texts lined up line by line for showing side by side: both sides have the same number of lines,
/// with filler lines opposite added or removed ones. Whitespace changes count (it's a diff of files).
/// </summary>
public sealed record SideBySideDiff(IReadOnlyList<DiffLine> Left, IReadOnlyList<DiffLine> Right, bool IsBinary)
{
    /// <summary>Blocks of changed lines (what "n changes" counts).</summary>
    public int ChangeCount => ChangeStarts().Count();

    /// <summary>The first line of each block of changed lines (for "next/previous change").</summary>
    public IEnumerable<int> ChangeStarts()
    {
        var inChange = false;
        for (var line = 0; line < Left.Count; line++)
        {
            var changed = IsChanged(line);
            if (changed && !inChange)
                yield return line;
            inChange = changed;
        }
    }

    public bool IsChanged(int line) => Left[line].Kind != DiffLineKind.Unchanged || Right[line].Kind != DiffLineKind.Unchanged;

    public static SideBySideDiff Compare(string left, string right)
    {
        if (left.Contains('\0') || right.Contains('\0'))
        {
            DiffLine[] message = [new(null, "Binary file: not shown.", DiffLineKind.Filler)];
            return new SideBySideDiff(message, message, IsBinary: true);
        }

        var model = SideBySideDiffBuilder.Diff(left, right, ignoreWhiteSpace: false);
        return new SideBySideDiff(Lines(model.OldText.Lines, DiffLineKind.Removed), Lines(model.NewText.Lines, DiffLineKind.Added), IsBinary: false);
    }

    private static List<DiffLine> Lines(IEnumerable<DiffPiece> pieces, DiffLineKind changed)
        => pieces.Select(piece => new DiffLine(piece.Position, piece.Text ?? "", piece.Type switch
        {
            ChangeType.Unchanged => DiffLineKind.Unchanged,
            ChangeType.Modified => DiffLineKind.Modified,
            ChangeType.Imaginary => DiffLineKind.Filler,
            _ => changed
        })).ToList();
}
