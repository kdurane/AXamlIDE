using System;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

/// <summary>Reading git log's output, and laying out the graph's lanes.</summary>
public sealed class GitGraphTests
{
    [Fact]
    public void Parses_log_records_and_their_refs()
    {
        var output = "aaa\u001fbbb ccc\u001fAnn\u001fann@example.com\u001f2026-10-27T09:08:57+01:00\u001f"
                     + "HEAD -> refs/heads/main, refs/remotes/origin/main, refs/remotes/origin/HEAD, tag: refs/tags/v1\u001fMerge it\u001f"
                     + "Why it was merged.\n\nSecond paragraph.\n\u001e\n"
                     + "bbb\u001f\u001fBob\u001fbob@example.com\u001f2026-10-26T10:00:00Z\u001f\u001fFirst\u001f\u001e\n";

        var commits = GitLogParser.ParseLog(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal(["bbb", "ccc"], commits[0].Parents);
        Assert.True(commits[0].IsMerge);
        Assert.Equal("Merge it", commits[0].Subject);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 9, 8, 57, TimeSpan.FromHours(1)), commits[0].Date);
        Assert.Equal(
            [new GitRef("main", GitRefKind.CurrentBranch), new GitRef("origin/main", GitRefKind.RemoteBranch), new GitRef("v1", GitRefKind.Tag)],
            commits[0].Refs);
        Assert.Equal("Why it was merged.\n\nSecond paragraph.", commits[0].Body);
        Assert.Equal("", commits[1].Body);
        Assert.Empty(commits[1].Parents);
        Assert.Empty(commits[1].Refs);
    }

    [Fact]
    public void A_detached_head_is_its_own_ref()
        => Assert.Equal([new GitRef("HEAD", GitRefKind.DetachedHead), new GitRef("feature", GitRefKind.Branch)],
            GitLogParser.ParseRefs("HEAD, refs/heads/feature"));

    [Fact]
    public void Parses_name_status_with_renames()
    {
        var files = GitLogParser.ParseNameStatus("M\0a.cs\0R087\0old.cs\0new.cs\0D\0gone.cs\0A\0added.cs\0");

        Assert.Equal(
            [
                new GitCommitFile("a.cs", GitFileState.Modified),
                new GitCommitFile("new.cs", GitFileState.Renamed, "old.cs"),
                new GitCommitFile("gone.cs", GitFileState.Deleted),
                new GitCommitFile("added.cs", GitFileState.Added)
            ],
            files);
    }

    [Fact]
    public void A_straight_history_is_one_lane()
    {
        var rows = GitGraphLayout.Layout([Commit("c", "b"), Commit("b", "a"), Commit("a")]);

        Assert.All(rows, row => Assert.Equal(0, row.Lane));
        Assert.Empty(rows[0].Top);                                   // the tip: nothing above
        Assert.Equal([new GitGraphLine(0, 0, 0)], rows[0].Bottom);
        Assert.Equal([new GitGraphLine(0, 0, 0)], rows[1].Top);
        Assert.Empty(rows[2].Bottom);                                // the first commit: nothing below
        Assert.All(rows, row => Assert.Equal(1, row.Width));
    }

    [Fact]
    public void A_merge_opens_a_lane_that_joins_back_where_the_branch_started()
    {
        //   m        merge of b2 into a2
        //   | \
        //   a2 |
        //   |  b2
        //   | /
        //   base
        var rows = GitGraphLayout.Layout([Commit("m", "a2", "b2"), Commit("a2", "base"), Commit("b2", "base"), Commit("base")]);

        var merge = rows[0];
        Assert.Equal(0, merge.Lane);
        Assert.Equal([new GitGraphLine(0, 0, 0), new GitGraphLine(0, 1, 1)], merge.Bottom);   // first parent straight down, second to a new lane

        Assert.Equal(0, rows[1].Lane);
        Assert.Contains(new GitGraphLine(1, 1, 1), rows[1].Top);   // the branch's lane passes by
        Assert.Equal(1, rows[2].Lane);                              // the branch's commit is in its lane

        var start = rows[3];
        Assert.Equal(0, start.Lane);
        Assert.Equal([new GitGraphLine(0, 0, 0), new GitGraphLine(1, 0, 1)], start.Top);   // both lanes end at the base
        Assert.Empty(start.Bottom);
        Assert.Equal(2, merge.Width);
    }

    [Fact]
    public void Two_branch_tips_get_their_own_lanes_and_colours()
    {
        var rows = GitGraphLayout.Layout([Commit("x", "base"), Commit("y", "base"), Commit("base")]);

        Assert.Equal(0, rows[0].Lane);
        Assert.Equal(1, rows[1].Lane);
        Assert.NotEqual(rows[0].Color, rows[1].Color);
        Assert.Equal([new GitGraphLine(0, 0, 0)], rows[1].Top);     // x's lane passes by y; y's own lane starts at y
        Assert.Equal(2, rows[2].Top.Count);
    }

    [Fact]
    public void A_continuation_carries_the_lanes_below_a_row_straight_on()
    {
        var merge = GitGraphLayout.Layout([Commit("m", "a", "b"), Commit("a"), Commit("b")])[0];

        var below = merge.Continuation();

        Assert.Equal(-1, below.Lane);
        Assert.Equal([new GitGraphLine(0, 0, 0), new GitGraphLine(1, 1, 1)], below.Top);
        Assert.Equal(below.Top, below.Bottom);
    }

    [Fact]
    public void A_branch_label_colours_its_line_from_there_down()
    {
        //   x        a feature tip, above main's tip
        //   |
        //   m        main (checked out): from here down the line is main's colour
        //   |
        //   base
        var commits = new[] { Commit("x", "m"), Commit("m", "base"), Commit("base") };

        var rows = GitGraphLayout.Layout(commits, commit => commit.Hash == "m" ? GitGraphLayout.CurrentBranchColor : null);

        Assert.Equal(0, rows[0].Color);                                                    // above main: a colour in turn
        Assert.Equal([new GitGraphLine(0, 0, 0)], rows[1].Top);                            // the line into main keeps it
        Assert.Equal(GitGraphLayout.CurrentBranchColor, rows[1].Color);
        Assert.Equal([new GitGraphLine(0, 0, GitGraphLayout.CurrentBranchColor)], rows[1].Bottom);
        Assert.Equal(GitGraphLayout.CurrentBranchColor, rows[2].Color);
    }

    [Fact]
    public void A_labelled_tip_starts_in_its_colour_without_using_one_in_turn()
    {
        var rows = GitGraphLayout.Layout([Commit("m", "base"), Commit("y", "base"), Commit("base")],
            commit => commit.Hash == "m" ? GitGraphLayout.UpstreamColor : null);

        Assert.Equal(GitGraphLayout.UpstreamColor, rows[0].Color);
        Assert.Equal(0, rows[1].Color);   // the next lane still gets the first colour
    }

    private static GitCommit Commit(string hash, params string[] parents)
        => new(hash, parents, "A", "a@example.com", DateTimeOffset.UnixEpoch, hash, []);
}
