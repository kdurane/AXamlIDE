using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

public sealed class MergeConflictsTests
{
    private const string Text = "before\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feature\nafter\n";

    [Fact]
    public void Finds_each_conflicts_markers()
    {
        var conflict = Assert.Single(MergeConflicts.Find(Text));
        Assert.Equal(new MergeConflict(2, null, 4, 6), conflict);
        Assert.True(conflict.Contains(3));
        Assert.False(conflict.Contains(7));

        Assert.Equal(new MergeConflict(1, 3, 5, 7),
            Assert.Single(MergeConflicts.Find("<<<<<<< HEAD\nours\n||||||| base\nold\n=======\ntheirs\n>>>>>>> b")));
        Assert.Empty(MergeConflicts.Find("<<<<<<<< not a marker\n=======\n>>>>>>> x\n"));
    }

    [Theory]
    [InlineData(ConflictChoice.Current, "before\nours\nafter\n")]
    [InlineData(ConflictChoice.Incoming, "before\ntheirs\nafter\n")]
    [InlineData(ConflictChoice.Both, "before\nours\ntheirs\nafter\n")]
    public void Resolving_keeps_the_chosen_side(ConflictChoice choice, string expected)
        => Assert.Equal(expected, Apply(Text, MergeConflicts.Resolve(Text, MergeConflicts.Find(Text)[0], choice)));

    [Fact]
    public void Resolving_drops_the_base_and_keeps_windows_line_endings()
    {
        const string text = "<<<<<<< HEAD\r\nours\r\n||||||| base\r\nold\r\n=======\r\ntheirs\r\n>>>>>>> b";
        Assert.Equal("ours\r\ntheirs", Apply(text, MergeConflicts.Resolve(text, MergeConflicts.Find(text)[0], ConflictChoice.Both)));
    }

    private static string Apply(string text, TextEdit edit) => text[..edit.Offset] + edit.Text + text[(edit.Offset + edit.Length)..];
}
