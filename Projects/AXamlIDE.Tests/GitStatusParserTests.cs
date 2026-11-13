using System.IO;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

public sealed class GitStatusParserTests
{
    // A real rooted folder on every OS (a bare "/repo" is not rooted on Windows); P() writes paths under it with /.
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"));
    private static string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

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
    public void Reads_the_head_commit_and_each_files_object_names()
    {
        var status = Parse("# branch.oid abc", "# branch.head main", "1 MM N... 100644 100644 100644 aaa bbb Both.cs");

        Assert.Equal("abc", status.HeadCommit);
        var change = status.ChangeFor(P("Both.cs"))!;
        Assert.Equal("aaa", change.HeadObject);
        Assert.Equal("bbb", change.IndexObject);
    }

    [Fact]
    public void Before_the_first_commit_there_is_no_head_commit()
        => Assert.Null(Parse("# branch.oid (initial)", "# branch.head main").HeadCommit);

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

        var staged = status.ChangeFor(P("src/Staged File.cs"))!;
        Assert.True(staged.IsStaged);
        Assert.False(staged.HasUnstagedChanges);

        var both = status.ChangeFor(P("Both.cs"))!;
        Assert.True(both.IsStaged);
        Assert.True(both.HasUnstagedChanges);

        Assert.Equal(GitFileState.Added, status.StateOfFile(P("New.cs")));
        Assert.Equal(GitFileState.Deleted, status.StateOfFile(P("Gone.cs")));
        Assert.Equal(GitFileState.Modified, status.StateOfFile(P("src/Changed.cs")));
        Assert.Equal(3, System.Linq.Enumerable.Count(status.Staged));
    }

    [Fact]
    public void Reads_renames_with_their_original_path()
    {
        var status = Parse("1 .M N... 100644 100644 100644 a b x.cs", "2 R. N... 100644 100644 100644 a b R100 New Name.cs", "Old Name.cs", "? after.txt");

        var rename = status.ChangeFor(P("New Name.cs"))!;
        Assert.Equal(GitFileState.Renamed, rename.State);
        Assert.Equal("Old Name.cs", rename.OriginalRelativePath);
        Assert.Equal(GitFileState.Untracked, status.StateOfFile(P("after.txt")));   // the record after the rename is still read
    }

    [Fact]
    public void Conflicts_are_unstaged_and_win_in_folders()
    {
        var status = Parse(
            "u UU N... 100644 100644 100644 100644 a b c src/Merge.cs",
            "? src/Untracked.cs",
            "1 .M N... 100644 100644 100644 a b src/Edit.cs");

        var conflict = status.ChangeFor(P("src/Merge.cs"))!;
        Assert.True(conflict.IsConflicted);
        Assert.False(conflict.IsStaged);
        Assert.True(conflict.HasUnstagedChanges);
        Assert.Equal(GitFileState.Conflicted, status.StateOfFolder(P("src")));
    }

    [Fact]
    public void Folders_take_the_strongest_state_inside_them()
    {
        var status = Parse("? a/b/New.cs", "1 .M N... 100644 100644 100644 a b a/Edit.cs");

        Assert.Equal(GitFileState.Untracked, status.StateOfFolder(P("a/b")));
        Assert.Equal(GitFileState.Modified, status.StateOfFolder(P("a")));
        Assert.Equal(GitFileState.None, status.StateOfFolder(P("other")));
    }

    [Fact]
    public void Files_under_ignored_folders_are_ignored()
    {
        var status = Parse("! bin/", "! secret.txt");

        Assert.Equal(GitFileState.Ignored, status.StateOfFolder(P("bin")));
        Assert.Equal(GitFileState.Ignored, status.StateOfFile(P("bin/Debug/app.dll")));
        Assert.Equal(GitFileState.Ignored, status.StateOfFile(P("secret.txt")));
        Assert.Equal(GitFileState.None, status.StateOfFile(P("binary.cs")));
    }
}
