using System;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

public class GitBlameTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(60 * 5, "5 minutes ago")]
    [InlineData(60 * 60, "1 hour ago")]
    [InlineData(60 * 60 * 30, "yesterday")]
    [InlineData(60 * 60 * 24 * 28, "28 days ago")]
    [InlineData(60 * 60 * 24 * 130, "4 months ago")]
    [InlineData(60 * 60 * 24 * 800, "2 years ago")]
    public void Ages_read_like_visual_studios(int secondsAgo, string expected)
        => Assert.Equal(expected, GitBlame.Ago(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void The_latest_change_of_a_span_wins()
    {
        GitBlameLine Line(string commit, int daysAgo) => new(commit, "A" + commit[0], Now.AddDays(-daysAgo), "");
        var lines = new[] { Line(new string('1', 40), 10), Line(new string('2', 40), 3), Line(new string('3', 40), 7) };

        Assert.Equal(lines[1], GitBlame.LastChange(lines, 1, 3));
        Assert.Equal(lines[2], GitBlame.LastChange(lines, 3, 9));
        Assert.Null(GitBlame.LastChange(lines, 5, 6));
    }
}
