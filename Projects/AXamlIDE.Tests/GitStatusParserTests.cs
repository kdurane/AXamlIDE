using System.IO;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

public sealed class GitStatusParserTests
{
    private const string Root = "/repo";

    private static GitStatus Parse(params string[] records) => GitStatusParser.Parse(string.Join('\0', records) + '\0', Root);

    [Fact]
    public void Reads_branch_upstream_and_ahead_behind()
    {
        var status = Parse("# branch.oid abc", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +2 -1");

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
    }

    [Fact]
    public void Detached_head_has_no_branch()
        => Assert.Null(Parse("# branch.oid abc", "# branch.head (detached)").Branch);

    [Fact]
    public void Reads_staged_and_unstaged_sides_and_paths_with_spaces()
    {
        var status = Parse(
            "1 M. N... 100644 100644 100644 aaa bbb src/Staged File.cs",
            "1 .M N... 100644 100644 100644 aaa bbb src/Changed.cs",
            "1 MM N... 100644 100644 100644 aaa bbb Both.cs",
            "1 A. N... 000000 100644 100644 000 bbb New.cs",
            "1 .D N... 100644 100644 000000 aaa bbb Gone.cs");

        var staged = status.ChangeFor("/repo/src/Staged File.cs")!;
        Assert.True(staged.IsStaged);
        Assert.False(staged.HasUnstagedChanges);

        var both = status.ChangeFor("/repo/Both.cs")!;
        Assert.True(both.IsStaged);
        Assert.True(both.HasUnstagedChanges);

        Assert.Equal(GitFileState.Added, status.StateOfFile("/repo/New.cs"));
        Assert.Equal(GitFileState.Deleted, status.StateOfFile("/repo/Gone.cs"));
        Assert.Equal(GitFileState.Modified, status.StateOfFile("/repo/src/Changed.cs"));
        Assert.Equal(3, System.Linq.Enumerable.Count(status.Staged));
    }

    [Fact]
    public void Reads_renames_with_their_original_path()
    {
        var status = Parse("1 .M N... 100644 100644 100644 a b x.cs", "2 R. N... 100644 100644 100644 a b R100 New Name.cs", "Old Name.cs", "? after.txt");

        var rename = status.ChangeFor("/repo/New Name.cs")!;
        Assert.Equal(GitFileState.Renamed, rename.State);
        Assert.Equal("Old Name.cs", rename.OriginalRelativePath);
        Assert.Equal(GitFileState.Untracked, status.StateOfFile("/repo/after.txt"));   // the record after the rename is still read
    }

    [Fact]
    public void Conflicts_are_unstaged_and_win_in_folders()
    {
        var status = Parse(
            "u UU N... 100644 100644 100644 100644 a b c src/Merge.cs",
            "? src/Untracked.cs",
            "1 .M N... 100644 100644 100644 a b src/Edit.cs");

        var conflict = status.ChangeFor("/repo/src/Merge.cs")!;
        Assert.True(conflict.IsConflicted);
        Assert.False(conflict.IsStaged);
        Assert.True(conflict.HasUnstagedChanges);
        Assert.Equal(GitFileState.Conflicted, status.StateOfFolder("/repo/src"));
    }

    [Fact]
    public void Folders_take_the_strongest_state_inside_them()
    {
        var status = Parse("? a/b/New.cs", "1 .M N... 100644 100644 100644 a b a/Edit.cs");

        Assert.Equal(GitFileState.Untracked, status.StateOfFolder("/repo/a/b"));
        Assert.Equal(GitFileState.Modified, status.StateOfFolder("/repo/a"));
        Assert.Equal(GitFileState.None, status.StateOfFolder("/repo/other"));
    }

    [Fact]
    public void Files_under_ignored_folders_are_ignored()
    {
        var status = Parse("! bin/", "! secret.txt");

        Assert.Equal(GitFileState.Ignored, status.StateOfFolder("/repo/bin"));
        Assert.Equal(GitFileState.Ignored, status.StateOfFile("/repo/bin/Debug/app.dll"));
        Assert.Equal(GitFileState.Ignored, status.StateOfFile("/repo/secret.txt"));
        Assert.Equal(GitFileState.None, status.StateOfFile("/repo/binary.cs"));
    }
}
