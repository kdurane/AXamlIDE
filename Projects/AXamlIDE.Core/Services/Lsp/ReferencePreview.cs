using System;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// One reference's line of code for a references list, cut into the text before the reference, the reference itself
/// (shown highlighted) and the text after, as VS Code's peek and Rider's usages list show it.
/// </summary>
public sealed record ReferencePreview(string Before, string Match, string After)
{
    /// <summary>Leading indentation goes; a long line keeps this much either side of the reference, with "…".</summary>
    public const int Context = 60;

    /// <summary>
    /// The preview of <paramref name="line"/> with characters <paramref name="start"/> to <paramref name="end"/> as the
    /// reference (both clamped to the line; a reference running past the end of the line stops there).
    /// </summary>
    public static ReferencePreview Of(string line, int start, int end)
    {
        start = Math.Clamp(start, 0, line.Length);
        end = Math.Clamp(end, start, line.Length);

        var from = 0;
        while (from < start && char.IsWhiteSpace(line[from]))
            from++;
        var to = line.Length;
        while (to > end && char.IsWhiteSpace(line[to - 1]))
            to--;

        var before = line[from..start];
        if (before.Length > Context)
            before = "…" + before[^Context..].TrimStart();
        var after = line[end..to];
        if (after.Length > Context)
            after = after[..Context].TrimEnd() + "…";

        return new ReferencePreview(before, line[start..end], after);
    }
}
