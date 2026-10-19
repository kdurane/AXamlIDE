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
    public async Task Failures_carry_gits_message()
    {
        var error = await Assert.ThrowsAsync<GitException>(() => _git.CommitAsync(_workspace.Root, "Nothing staged"));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    private void Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _workspace.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
