using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Git;

/// <summary>
/// A line drawn through half a row of the Git graph, from one lane to another (the same lane for a straight line).
/// In <see cref="GitGraphRow.Top"/> it runs from the row's top edge to its middle; in <see cref="GitGraphRow.Bottom"/>
/// from the middle to the bottom edge. <see cref="Color"/> counts up from 0 and the view picks a colour from it in
/// turn, except for <see cref="GitGraphLayout.CurrentBranchColor"/> and <see cref="GitGraphLayout.UpstreamColor"/>.
/// </summary>
public sealed record GitGraphLine(int FromLane, int ToLane, int Color);

/// <summary>
/// How one commit's row of the graph is drawn: its dot (in <see cref="Lane"/>, -1 for none) and the lines through it.
/// </summary>
public sealed record GitGraphRow(int Lane, int Color, IReadOnlyList<GitGraphLine> Top, IReadOnlyList<GitGraphLine> Bottom)
{
    /// <summary>How many lanes wide the row is.</summary>
    public int Width => Top.Concat(Bottom).Select(line => Math.Max(line.FromLane, line.ToLane))
        .Append(Lane).DefaultIfEmpty(-1).Max() + 1;

    /// <summary>
    /// A row without a commit, under this one (the changed files of an expanded commit): the lines leaving the
    /// bottom of this row carry straight on through it.
    /// </summary>
    public GitGraphRow Continuation()
    {
        var lanes = Bottom.GroupBy(line => line.ToLane)
            .Select(group => new GitGraphLine(group.Key, group.Key,
                group.FirstOrDefault(line => line.FromLane == line.ToLane)?.Color ?? group.First().Color))
            .ToList();
        return new GitGraphRow(-1, 0, lanes, lanes);
    }
}

/// <summary>
/// Lays out the Git graph: which lane (column) each commit's dot sits in, and the lines joining it to its parents,
/// as VS Code's Source Control Graph draws it.
///
/// It goes down the commits (newest first; children always come before their parents) keeping a list of lanes, each
/// waiting for the commit it leads to. For each commit:
///   1. its dot goes in the first lane waiting for it, or a new lane (a branch tip nothing above leads to);
///   2. any other lanes waiting for it end here, joining the dot (two branches meeting at the commit they came from);
///   3. its lane now waits for its first parent, and each other parent (a merge) gets a lane: one already waiting
///      for that parent, or a new one;
///   4. every other lane carries straight on.
/// A new lane takes the first free column, so the graph stays narrow, and a new colour.
///
/// Like VS Code, the current branch's line is drawn in its label's colour, and so is its upstream's: at the commit the
/// label is on, the lane switches to <paramref name="labelColor"/>'s colour from there down (the line above keeps the
/// colour it had).
/// </summary>
public static class GitGraphLayout
{
    /// <summary>The colour of the current branch's line (its label's colour), not one of the colours in turn.</summary>
    public const int CurrentBranchColor = -1;

    /// <summary>The colour of the current branch's upstream's line ("origin/main").</summary>
    public const int UpstreamColor = -2;

    /// <param name="commits">Newest first, children before parents.</param>
    /// <param name="labelColor">
    /// For a commit with a label whose line has its own colour (<see cref="CurrentBranchColor"/>,
    /// <see cref="UpstreamColor"/>), that colour; else null.
    /// </param>
    public static IReadOnlyList<GitGraphRow> Layout(IReadOnlyList<GitCommit> commits, Func<GitCommit, int?>? labelColor = null)
    {
        var lanes = new List<string?>();   // the commit each lane is waiting for; null for a free column
        var colors = new List<int>();
        var nextColor = 0;
        var rows = new List<GitGraphRow>(commits.Count);

        foreach (var commit in commits)
        {
            // 1 and 2: the lines coming in at the top.
            var lane = -1;
            var top = new List<GitGraphLine>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is null)
                    continue;
                if (lanes[i] != commit.Hash)
                {
                    top.Add(new GitGraphLine(i, i, colors[i]));
                    continue;
                }

                if (lane < 0)
                    lane = i;
                top.Add(new GitGraphLine(i, lane, colors[i]));
                if (i != lane)
                    lanes[i] = null;   // this branch ends here
            }

            var ownColor = labelColor?.Invoke(commit);
            if (lane < 0)   // nothing above leads here: a branch tip
                lane = NewLane(lanes, colors, commit.Hash, ownColor ?? nextColor++);
            else if (ownColor is { } switched)
                colors[lane] = switched;   // the line below a branch's label takes the label's colour
            var color = colors[lane];

            // 3: wait for the parents.
            lanes[lane] = commit.Parents.Count > 0 ? commit.Parents[0] : null;
            var merged = new List<int>();
            var started = new HashSet<int>();
            foreach (var parent in commit.Parents.Skip(1))
            {
                var target = lanes.IndexOf(parent);
                if (target < 0)
                {
                    target = NewLane(lanes, colors, parent, nextColor++);
                    started.Add(target);
                }
                merged.Add(target);
            }

            // 3 and 4: the lines going out at the bottom.
            var bottom = new List<GitGraphLine>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is not null && !started.Contains(i))
                    bottom.Add(new GitGraphLine(i, i, colors[i]));
            foreach (var target in merged)
                bottom.Add(new GitGraphLine(lane, target, colors[target]));

            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                colors.RemoveAt(colors.Count - 1);
            }

            rows.Add(new GitGraphRow(lane, color, top, bottom));
        }
        return rows;
    }

    /// <summary>Take the first free column (or add one) for a lane waiting for <paramref name="hash"/>.</summary>
    private static int NewLane(List<string?> lanes, List<int> colors, string hash, int color)
    {
        var free = lanes.IndexOf(null);
        if (free < 0)
        {
            lanes.Add(hash);
            colors.Add(color);
            return lanes.Count - 1;
        }
        lanes[free] = hash;
        colors[free] = color;
        return free;
    }
}
