using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Tests;

/// <summary>Runs the real git against a throwaway repository.</summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly GitService _git = new();

    public GitServiceTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Finds_the_repository_root_from_a_subfolder()
    {
        var file = _workspace.Write("src/A.cs", "class A {}");

        Assert.Equal(Path.GetFullPath(_workspace.Root), await _git.FindRepositoryRootAsync(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public async Task Blame_takes_the_editors_text_and_marks_unsaved_lines_as_not_committed()
    {
        _workspace.Write("A.cs", "a\nb\n");
        Git("add", "A.cs");
        await _git.CommitAsync(_workspace.Root, "First");

        var lines = await _git.BlameAsync(_workspace.Root, "A.cs", "a\nb\nc\n");

        Assert.Equal(3, lines.Count);
        Assert.Equal(("Test", "First", true), (lines[0].Author, lines[0].Summary, lines[0].IsCommitted));
        Assert.Equal(lines[0].Commit, lines[1].Commit);
        Assert.False(lines[2].IsCommitted);
        Assert.Equal("Not committed yet", GitBlame.Describe(GitBlame.LastChange(lines, 1, 3)!, DateTimeOffset.Now));
        Assert.StartsWith("Test, ", GitBlame.Describe(GitBlame.LastChange(lines, 1, 2)!, DateTimeOffset.Now));
    }

    [Fact]
    public async Task Outside_a_repository_there_is_no_root()
    {
        using var other = new TempWorkspace();
        Assert.Null(await _git.FindRepositoryRootAsync(other.Root));
    }

    [Fact]
    public async Task Stage_unstage_and_commit()
    {
        var file = _workspace.Write("A.cs", "class A {}");

        var status = await _git.GetStatusAsync(_workspace.Root);
        Assert.Equal("main", status.Branch);
        Assert.Equal(GitFileState.Untracked, status.StateOfFile(file));

        await _git.StageAsync(_workspace.Root, [file]);
        Assert.True((await _git.GetStatusAsync(_workspace.Root)).ChangeFor(file)!.IsStaged);

        await _git.UnstageAsync(_workspace.Root, [file]);   // before the first commit there is no HEAD to restore from
        Assert.Equal(GitFileState.Untracked, (await _git.GetStatusAsync(_workspace.Root)).StateOfFile(file));

        await _git.StageAsync(_workspace.Root, [file]);
        await _git.CommitAsync(_workspace.Root, "First");
        Assert.Empty((await _git.GetStatusAsync(_workspace.Root)).Changes);

        File.WriteAllText(file, "class A { int x; }");
        await _git.StageAsync(_workspace.Root, [file]);
        await _git.UnstageAsync(_workspace.Root, [file]);
        var change = (await _git.GetStatusAsync(_workspace.Root)).ChangeFor(file)!;
        Assert.False(change.IsStaged);
        Assert.Equal(GitFileState.Modified, change.WorkTreeState);
        Assert.Equal("class A {}", await _git.ReadCommittedAsync(_workspace.Root, "A.cs"));
    }

    [Fact]
    public async Task Ignored_folders_are_reported()
    {
        _workspace.Write(".gitignore", "bin/\n");
        var dll = _workspace.Write("bin/app.dll", "x");

        var status = await _git.GetStatusAsync(_workspace.Root);

        Assert.Equal(GitFileState.Ignored, status.StateOfFile(dll));
        Assert.DoesNotContain(status.Changes, change => change.FullPath == dll);
    }

    [Fact]
    public async Task Ignore_then_unignore_a_file_removes_the_rule()
    {
        var file = _workspace.Write("notes.txt", "x");
        _workspace.Write(".gitignore", "*.log");   // no newline at the end

        await _git.IgnoreAsync(_workspace.Root, file);
        Assert.Equal("*.log\n/notes.txt\n", File.ReadAllText(Path.Combine(_workspace.Root, ".gitignore")));
        Assert.Equal(GitFileState.Ignored, (await _git.GetStatusAsync(_workspace.Root)).StateOfFile(file));

        Assert.Null(await _git.UnignoreAsync(_workspace.Root, file));
        Assert.Equal("*.log\n", File.ReadAllText(Path.Combine(_workspace.Root, ".gitignore")));
        Assert.Equal(GitFileState.Untracked, (await _git.GetStatusAsync(_workspace.Root)).StateOfFile(file));
    }

    [Fact]
    public async Task Unignoring_one_file_of_a_pattern_adds_an_exception()
    {
        _workspace.Write(".gitignore", "*.log\n");
        var keep = _workspace.Write("keep.log", "x");
        var other = _workspace.Write("other.log", "x");

        Assert.Null(await _git.UnignoreAsync(_workspace.Root, keep));

        var status = await _git.GetStatusAsync(_workspace.Root);
        Assert.Equal(GitFileState.Untracked, status.StateOfFile(keep));
        Assert.Equal(GitFileState.Ignored, status.StateOfFile(other));
        Assert.EndsWith("!/keep.log\n", File.ReadAllText(Path.Combine(_workspace.Root, ".gitignore")));
    }

    [Fact]
    public async Task Folders_are_ignored_with_a_trailing_slash()
    {
        var folder = Path.GetDirectoryName(_workspace.Write("out/a.txt", "x"))!;

        await _git.IgnoreAsync(_workspace.Root, folder);

        Assert.Contains("/out/\n", File.ReadAllText(Path.Combine(_workspace.Root, ".gitignore")));
        Assert.Equal(GitFileState.Ignored, (await _git.GetStatusAsync(_workspace.Root)).StateOfFolder(folder));
    }

    [Fact]
    public async Task A_file_inside_an_ignored_folder_cant_be_unignored_alone()
    {
        _workspace.Write(".gitignore", "bin/\n");
        var dll = _workspace.Write("bin/app.dll", "x");

        Assert.NotNull(await _git.UnignoreAsync(_workspace.Root, dll));
    }

    [Fact]
    public async Task Publish_push_and_pull_through_a_remote()
    {
        using var remote = new TempWorkspace();
        GitIn(remote.Root, "init", "-q", "--bare", "-b", "main");
        Git("remote", "add", "origin", remote.Root);
        var file = _workspace.Write("A.cs", "one");
        await _git.StageAsync(_workspace.Root, [file]);
        await _git.CommitAsync(_workspace.Root, "First");

        Assert.Equal(["origin"], await _git.GetRemotesAsync(_workspace.Root));
        await _git.PushAsync(_workspace.Root, hasUpstream: false, remoteForNewBranch: "origin");   // publish
        var status = await _git.GetStatusAsync(_workspace.Root);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);

        // Someone else pushes; we fetch (behind 1) and pull.
        using var other = new TempWorkspace();
        GitIn(other.Root, "clone", "-q", remote.Root, ".");
        GitIn(other.Root, "-c", "user.name=O", "-c", "user.email=o@example.com", "commit", "-q", "--allow-empty", "-m", "Theirs");
        GitIn(other.Root, "push", "-q");

        await _git.FetchAsync(_workspace.Root);
        Assert.Equal(1, (await _git.GetStatusAsync(_workspace.Root)).Behind);
        await _git.PullAsync(_workspace.Root);
        Assert.Equal(0, (await _git.GetStatusAsync(_workspace.Root)).Behind);
    }

    [Fact]
    public async Task Branches_switch_and_create()
    {
        await CommitFileAsync("A.cs", "one");

        await _git.CreateBranchAsync(_workspace.Root, "feature");
        Assert.Equal("feature", (await _git.GetStatusAsync(_workspace.Root)).Branch);
        Assert.Equal(["feature", "main"], await _git.GetBranchesAsync(_workspace.Root));

        await _git.SwitchBranchAsync(_workspace.Root, "main");
        Assert.Equal("main", (await _git.GetStatusAsync(_workspace.Root)).Branch);
    }

    [Fact]
    public async Task Stash_and_pop_keep_new_files()
    {
        await CommitFileAsync("A.cs", "one");
        var added = _workspace.Write("New.cs", "new");

        await _git.StashAsync(_workspace.Root);
        Assert.False(File.Exists(added));
        await _git.PopStashAsync(_workspace.Root);
        Assert.True(File.Exists(added));
    }

    [Fact]
    public async Task Discard_all_keeps_new_files_unless_asked_and_amend_keeps_one_commit()
    {
        var file = await CommitFileAsync("A.cs", "one");
        File.WriteAllText(file, "changed");
        var added = _workspace.Write("New.cs", "new");
        var staged = _workspace.Write("Staged.cs", "staged");
        await _git.StageAsync(_workspace.Root, [staged]);

        await _git.DiscardAllAsync(_workspace.Root, deleteUntracked: false);
        Assert.Equal("one", File.ReadAllText(file));
        Assert.True(File.Exists(added));
        Assert.False(File.Exists(staged));   // added since the last commit: gone, as the commit doesn't have it

        await _git.StageAsync(_workspace.Root, [added]);
        await _git.CommitAsync(_workspace.Root, "", amend: true);
        Assert.Empty((await _git.GetStatusAsync(_workspace.Root)).Changes);
        Assert.Equal("1", GitOutput("rev-list", "--count", "HEAD").Trim());

        File.WriteAllText(file, "changed again");
        var untracked = _workspace.Write("Folder/Untracked.cs", "new");
        await _git.DiscardAllAsync(_workspace.Root, deleteUntracked: true);
        Assert.Equal("one", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(untracked)));
        Assert.Empty((await _git.GetStatusAsync(_workspace.Root)).Changes);
    }

    [Fact]
    public async Task Undo_changes_by_file_on_each_side()
    {
        var file = await CommitFileAsync("A.cs", "one");
        var other = await CommitFileAsync("B.cs", "b");

        // Unstaged: back to what's staged.
        File.WriteAllText(file, "staged");
        await _git.StageAsync(_workspace.Root, [file]);
        File.WriteAllText(file, "staged, then changed");
        await _git.DiscardUnstagedAsync(_workspace.Root, [file]);
        Assert.Equal("staged", File.ReadAllText(file));

        // Staged: back to the last commit, on both sides; a rename goes back to its old name.
        await _git.UndoChangesAsync(_workspace.Root, [file]);
        Assert.Equal("one", File.ReadAllText(file));
        Git("mv", "B.cs", "C.cs");
        await _git.UndoChangesAsync(_workspace.Root, [Path.Combine(_workspace.Root, "C.cs"), other]);
        Assert.True(File.Exists(other));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "C.cs")));

        // New files: deleted.
        var added = _workspace.Write("New.cs", "new");
        await _git.DeleteUntrackedAsync(_workspace.Root, [added]);
        Assert.False(File.Exists(added));
        Assert.Empty((await _git.GetStatusAsync(_workspace.Root)).Changes);
    }

    [Fact]
    public async Task Undo_changes_before_the_first_commit_deletes_added_files()
    {
        var file = _workspace.Write("A.cs", "one");
        await _git.StageAsync(_workspace.Root, [file]);

        await _git.UndoChangesAsync(_workspace.Root, [file]);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Undo_last_commit_keeps_its_changes_staged()
    {
        await CommitFileAsync("A.cs", "one");
        var second = await CommitFileAsync("B.cs", "two");

        await _git.UndoLastCommitAsync(_workspace.Root);
        Assert.Equal("1", GitOutput("rev-list", "--count", "HEAD").Trim());
        Assert.True((await _git.GetStatusAsync(_workspace.Root)).ChangeFor(second)!.IsStaged);

        Git("commit", "-q", "-m", "Again");
        Git("reset", "-q", "--soft", "HEAD~1");
        await _git.UndoLastCommitAsync(_workspace.Root);   // the first commit: no commits left, both files staged
        var status = await _git.GetStatusAsync(_workspace.Root);
        Assert.Null(status.HeadCommit);
        Assert.Equal(2, status.Staged.Count());
    }

    [Fact]
    public async Task Branches_merge_rename_and_delete()
    {
        await CommitFileAsync("A.cs", "one");
        await _git.CreateBranchAsync(_workspace.Root, "feature");
        var b = await CommitFileAsync("B.cs", "two");
        await _git.SwitchBranchAsync(_workspace.Root, "main");

        await Assert.ThrowsAsync<GitException>(() => _git.DeleteBranchAsync(_workspace.Root, "feature", force: false));   // not merged
        await _git.MergeAsync(_workspace.Root, "feature");
        Assert.True(File.Exists(b));

        await _git.RenameBranchAsync(_workspace.Root, "feature", "done");
        Assert.Equal(["done", "main"], await _git.GetLocalBranchesAsync(_workspace.Root));
        await _git.DeleteBranchAsync(_workspace.Root, "done", force: false);
        Assert.Equal(["main"], await _git.GetLocalBranchesAsync(_workspace.Root));
    }

    [Fact]
    public async Task Stashes_list_apply_and_drop()
    {
        var file = await CommitFileAsync("A.cs", "one");
        File.WriteAllText(file, "first");
        await _git.StashAsync(_workspace.Root, "First try");
        File.WriteAllText(file, "second");
        await _git.StashAsync(_workspace.Root);

        var stashes = await _git.GetStashesAsync(_workspace.Root);
        Assert.Equal(2, stashes.Count);
        Assert.StartsWith("stash@{1}: On main: First try", stashes[1]);

        await _git.ApplyStashAsync(_workspace.Root, "stash@{1}", drop: false);
        Assert.Equal("first", File.ReadAllText(file));
        Assert.Equal(2, (await _git.GetStashesAsync(_workspace.Root)).Count);

        await _git.DropStashAsync(_workspace.Root, "stash@{1}");
        Assert.Single(await _git.GetStashesAsync(_workspace.Root));
    }

    [Fact]
    public async Task Commits_revert_cherry_pick_tag_reset_and_check_out()
    {
        var file = await CommitFileAsync("A.cs", "one");
        var first = GitOutput("rev-parse", "HEAD").Trim();
        File.WriteAllText(file, "two");
        Git("commit", "-q", "-am", "Two");
        var two = GitOutput("rev-parse", "HEAD").Trim();

        await _git.RevertCommitAsync(_workspace.Root, two);
        Assert.Equal("one", File.ReadAllText(file));

        await _git.CreateBranchAsync(_workspace.Root, "old", first);
        Assert.Equal("old", (await _git.GetStatusAsync(_workspace.Root)).Branch);
        await _git.CherryPickAsync(_workspace.Root, two);
        Assert.Equal("two", File.ReadAllText(file));

        await _git.CreateTagAsync(_workspace.Root, "v1", first);
        Assert.Equal(first, GitOutput("rev-parse", "v1").Trim());

        File.WriteAllText(file, "uncommitted");
        await _git.ResetAsync(_workspace.Root, first, GitResetMode.Hard);
        Assert.Equal("one", File.ReadAllText(file));
        Assert.Equal(first, GitOutput("rev-parse", "HEAD").Trim());

        await _git.CheckoutCommitAsync(_workspace.Root, two);
        Assert.Null((await _git.GetStatusAsync(_workspace.Root)).Branch);   // detached
    }

    [Fact]
    public async Task Abort_stops_a_merge_with_conflicts()
    {
        var file = await CommitFileAsync("A.cs", "one");
        Assert.Null(await _git.AbortOperationAsync(_workspace.Root));

        await _git.CreateBranchAsync(_workspace.Root, "feature");
        File.WriteAllText(file, "feature");
        Git("commit", "-q", "-am", "Feature");
        await _git.SwitchBranchAsync(_workspace.Root, "main");
        File.WriteAllText(file, "main");
        Git("commit", "-q", "-am", "Main");
        await Assert.ThrowsAsync<GitException>(() => _git.MergeAsync(_workspace.Root, "feature"));

        Assert.Equal("merge", await _git.AbortOperationAsync(_workspace.Root));
        Assert.Equal("main", File.ReadAllText(file));

        await Assert.ThrowsAsync<GitException>(() => _git.MergeAsync(_workspace.Root, "feature"));
        Assert.True((await _git.GetStatusAsync(_workspace.Root)).ChangeFor(file)!.IsConflicted);
        await _git.TakeConflictSideAsync(_workspace.Root, [file], current: false);
        Assert.Equal("feature", File.ReadAllText(file));
        Assert.False((await _git.GetStatusAsync(_workspace.Root)).ChangeFor(file)!.IsConflicted);
    }

    [Fact]
    public async Task Log_lists_a_merge_with_its_branches_and_files()
    {
        Assert.Empty(await _git.GetLogAsync(_workspace.Root, allBranches: true, upstream: null, maxCount: 10));   // no commits yet

        await CommitFileAsync("A.cs", "one");
        await _git.CreateBranchAsync(_workspace.Root, "feature");
        await CommitFileAsync("B.cs", "two");
        await _git.SwitchBranchAsync(_workspace.Root, "main");
        File.WriteAllText(Path.Combine(_workspace.Root, "A.cs"), "one, changed");
        Git("commit", "-q", "-am", "Change A");
        Git("tag", "v1");
        Git("merge", "-q", "--no-ff", "-m", "Merge feature", "feature");

        var log = await _git.GetLogAsync(_workspace.Root, allBranches: false, upstream: null, maxCount: 10);

        Assert.Equal(4, log.Count);
        var merge = log[0];
        Assert.Equal("Merge feature", merge.Subject);
        Assert.True(merge.IsMerge);
        Assert.Contains(new GitRef("main", GitRefKind.CurrentBranch), merge.Refs);
        Assert.Contains(log, commit => commit.Refs.Contains(new GitRef("feature", GitRefKind.Branch)));
        Assert.Contains(log, commit => commit.Refs.Contains(new GitRef("v1", GitRefKind.Tag)));
        Assert.Equal("Test", merge.AuthorName);

        var first = log[^1];
        Assert.Equal([new GitCommitFile("A.cs", GitFileState.Added)], await _git.GetCommitFilesAsync(_workspace.Root, first));
        var changeA = log.Single(commit => commit.Subject == "Change A");
        Assert.Equal([new GitCommitFile("A.cs", GitFileState.Modified)], await _git.GetCommitFilesAsync(_workspace.Root, changeA));
        Assert.Equal("one", await _git.ReadAtCommitAsync(_workspace.Root, first.Hash, "A.cs"));
        Assert.Null(await _git.ReadAtCommitAsync(_workspace.Root, first.Hash, "B.cs"));

        var before = await _git.GetRefsSnapshotAsync(_workspace.Root);
        Assert.Equal(before, await _git.GetRefsSnapshotAsync(_workspace.Root));
        await CommitFileAsync("C.cs", "three");
        Assert.NotEqual(before, await _git.GetRefsSnapshotAsync(_workspace.Root));
    }

    private async Task<string> CommitFileAsync(string name, string text)
    {
        var file = _workspace.Write(name, text);
        await _git.StageAsync(_workspace.Root, [file]);
        await _git.CommitAsync(_workspace.Root, "Add " + name);
        return file;
    }

    [Fact]
    public async Task Failures_carry_gits_message()
    {
        var error = await Assert.ThrowsAsync<GitException>(() => _git.CommitAsync(_workspace.Root, "Nothing staged"));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task The_diff_is_the_staged_change_or_everything_since_the_last_commit()
    {
        _workspace.Write("a.txt", "one\n");
        _workspace.Write("b.txt", "one\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "first");
        _workspace.Write("a.txt", "two\n");
        _workspace.Write("b.txt", "two\n");
        Git("add", "a.txt");

        var staged = await _git.GetDiffAsync(_workspace.Root, staged: true);
        var all = await _git.GetDiffAsync(_workspace.Root, staged: false);

        Assert.Contains("+two", staged);
        Assert.Contains("a.txt", staged);
        Assert.DoesNotContain("b.txt", staged);
        Assert.Contains("a.txt", all);
        Assert.Contains("b.txt", all);
    }

    [Fact]
    public async Task Before_the_first_commit_the_diff_is_the_staged_files_and_there_are_no_subjects()
    {
        _workspace.Write("a.txt", "one\n");
        Git("add", "-A");

        Assert.Contains("+one", await _git.GetDiffAsync(_workspace.Root, staged: true));
        Assert.Empty(await _git.GetRecentSubjectsAsync(_workspace.Root, 10));
    }

    [Fact]
    public async Task Recent_subjects_are_the_first_lines_newest_first()
    {
        _workspace.Write("a.txt", "one\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "feat: first\n\nBody.");
        _workspace.Write("a.txt", "two\n");
        Git("commit", "-q", "-am", "fix: second");

        Assert.Equal(["fix: second", "feat: first"], await _git.GetRecentSubjectsAsync(_workspace.Root, 10));
    }

    private void Git(params string[] arguments) => GitIn(_workspace.Root, arguments);

    private string GitOutput(params string[] arguments) => GitIn(_workspace.Root, arguments);

    private static string GitIn(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }
}
