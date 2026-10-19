using System.Linq;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

public sealed class SideBySideDiffTests
{
    [Fact]
    public void Both_sides_line_up_with_filler_opposite_added_lines()
    {
        var diff = SideBySideDiff.Compare("a\nb\n", "a\nnew\nb\n");

        Assert.Equal(diff.Left.Count, diff.Right.Count);
        var added = diff.Right.Single(line => line.Kind == DiffLineKind.Added);
        Assert.Equal("new", added.Text);
        Assert.Equal(2, added.Number);
        Assert.Equal(DiffLineKind.Filler, diff.Left[diff.Right.ToList().IndexOf(added)].Kind);
        Assert.Equal(1, diff.ChangeCount);
    }

    [Fact]
    public void Removed_and_changed_lines()
    {
        var diff = SideBySideDiff.Compare("keep\ngone\nold\nkeep2\n", "keep\nchanged\nkeep2\n");

        Assert.Contains(diff.Left, line => line.Kind is DiffLineKind.Removed or DiffLineKind.Modified && line.Text == "gone");
        Assert.Contains(diff.Right, line => line.Kind is DiffLineKind.Added or DiffLineKind.Modified && line.Text == "changed");
        Assert.All(diff.Left.Where(line => line.Text.StartsWith("keep")), line => Assert.Equal(DiffLineKind.Unchanged, line.Kind));
    }

    [Fact]
    public void Whitespace_changes_count()
        => Assert.Equal(1, SideBySideDiff.Compare("int x;\n", "int  x;\n").ChangeCount);

    [Fact]
    public void Separate_blocks_are_counted_separately()
        => Assert.Equal(2, SideBySideDiff.Compare("a\nb\nc\nd\ne\n", "A\nb\nc\nd\nE\n").ChangeCount);

    [Fact]
    public void A_new_file_is_all_added()
    {
        var diff = SideBySideDiff.Compare("", "one\ntwo\n");
        Assert.All(diff.Right.Where(line => line.Text.Length > 0), line => Assert.Equal(DiffLineKind.Added, line.Kind));
    }

    [Fact]
    public void Binary_files_are_not_shown()
        => Assert.True(SideBySideDiff.Compare("a\0b", "a").IsBinary);
}
