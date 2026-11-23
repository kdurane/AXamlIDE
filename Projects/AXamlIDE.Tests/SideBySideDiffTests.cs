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

    [Fact]
    public void A_changed_line_marks_only_the_words_that_changed()
    {
        var diff = SideBySideDiff.Compare("foo(x, y);\n", "foo(x, z, y);\n");

        var left = diff.Left.Single(line => line.Kind == DiffLineKind.Modified);
        var right = diff.Right.Single(line => line.Kind == DiffLineKind.Modified);
        Assert.Empty(left.ChangedSpans!);
        var span = Assert.Single(right.ChangedSpans!);
        Assert.Equal("z, ", right.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Added_and_removed_lines_have_no_word_spans()
    {
        var diff = SideBySideDiff.Compare("a\n", "a\nb\n");

        Assert.All(diff.Right, line => Assert.Null(line.ChangedSpans));
    }
}

public sealed class LineChangesTests
{
    [Fact]
    public void Added_modified_and_deleted_blocks_are_in_the_current_texts_lines()
    {
        var changes = LineChanges.Compare("1\n2\n3\n4\n5\n", "1\nTWO\n3\nnew\n4\n");

        Assert.Equal(
        [
            (2, 1, LineChangeKind.Modified),
            (4, 1, LineChangeKind.Added),
            (5, 0, LineChangeKind.Deleted),   // "5" went from below line 5 ("4")
        ], changes.Select(change => (change.FirstLine, change.LineCount, change.Kind)));
        Assert.Equal(["2"], LineChanges.BaseLines("1\n2\n3\n4\n5\n", changes[0]));
        Assert.Empty(LineChanges.BaseLines("1\n2\n3\n4\n5\n", changes[1]));
        Assert.Equal(["5"], LineChanges.BaseLines("1\n2\n3\n4\n5\n", changes[2]));
    }

    [Fact]
    public void Deleting_the_first_line_marks_above_line_one()
    {
        var change = Assert.Single(LineChanges.Compare("a\nb\n", "b\n"));
        Assert.Equal((0, 0, LineChangeKind.Deleted), (change.FirstLine, change.LineCount, change.Kind));
    }

    [Theory]
    [InlineData("1\n2\n3\n", "1\nTWO\n3\n")]              // modified
    [InlineData("1\n2\n3\n", "1\n2\nnew\n3\n")]          // added in the middle
    [InlineData("1\n2\n", "1\n2\nnew\nmore\n")]           // added at the end
    [InlineData("1\n2", "1\n2\nnew")]                        // added at the end, no final line ending
    [InlineData("1\n2\n", "new\n1\n2\n")]                  // added at the top
    [InlineData("1\n2\n3\n", "1\n3\n")]                    // deleted
    [InlineData("1\n2\n3\n", "2\n3\n")]                    // deleted at the top
    [InlineData("1\n2\n3", "1\n2")]                          // deleted at the end
    [InlineData("1\r\n2\r\n3\r\n", "1\r\nTWO\r\nTOO\r\n3\r\n")]   // Windows line endings
    public void Reverting_every_change_gives_back_the_base(string baseText, string text)
    {
        var normalizedBase = baseText.ReplaceLineEndings("\n");
        var changes = LineChanges.Compare(normalizedBase, text.ReplaceLineEndings("\n"));
        Assert.NotEmpty(changes);
        // From the bottom up, so the earlier changes' lines don't move.
        foreach (var change in changes.Reverse())
        {
            var edit = LineChanges.Revert(text, change, LineChanges.BaseLines(normalizedBase, change));
            text = text[..edit.Offset] + edit.Text + text[(edit.Offset + edit.Length)..];
        }
        Assert.Equal(baseText, text);
    }

    [Fact]
    public void The_same_text_has_no_changes()
    {
        Assert.Empty(LineChanges.Compare("a\nb\n", "a\nb\n"));
    }
}
