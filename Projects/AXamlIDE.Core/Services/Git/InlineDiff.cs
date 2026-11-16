using System.Collections.Generic;
using System.Linq;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace AXamlIDE.Services.Git;

/// <summary>
/// Two texts as one list of lines, removed lines just before the lines that replaced them, as in a unified diff
/// ("-old", "+new"). The Claude panel shows an edit this way. Lines are Unchanged, Removed or Added, and
/// <see cref="DiffLine.Number"/> is null: the texts are often just the changed part of a file.
/// </summary>
public static class InlineDiff
{
    /// <param name="oldText">The text before, or null for a new file (every line is added).</param>
    public static IReadOnlyList<DiffLine> Compare(string? oldText, string newText)
    {
        var model = InlineDiffBuilder.Diff(oldText ?? "", newText, ignoreWhiteSpace: false);
        var lines = model.Lines
            .Where(piece => piece.Type != ChangeType.Imaginary)
            .Select(piece => new DiffLine(null, piece.Text ?? "", piece.Type switch
            {
                ChangeType.Deleted => DiffLineKind.Removed,
                ChangeType.Inserted => DiffLineKind.Added,
                _ => DiffLineKind.Unchanged
            }))
            .ToList();
        // A new file's empty "before" isn't a removed blank line.
        if (oldText is null or "" && lines.Count > 0 && lines[0] is { Kind: DiffLineKind.Removed, Text: "" })
            lines.RemoveAt(0);
        return lines;
    }
}
