using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Git;

/// <summary>A git command failed; the message is git's own error text.</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>What git reset does with the changes after the commit it moves the branch to.</summary>
public enum GitResetMode
{
    /// <summary>Keep them, staged.</summary>
    Soft,
    /// <summary>Keep them, not staged (git's default).</summary>
    Mixed,
    /// <summary>Throw them away, and every uncommitted change.</summary>
    Hard
}

/// <summary>
/// Git through the <c>git</c> command line (as VS Code does): it follows the user's git config, hooks and
/// credential helpers, and needs no native library. Every method runs one git process; nothing is cached.
/// </summary>
public sealed class GitService
{
    /// <summary>The root of the repository containing <paramref name="path"/>, or null (not in a repository, or no git).</summary>
    public async Task<string?> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (directory is null)
            return null;
        try
        {
            var root = (await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
            return root.Length > 0 ? Path.GetFullPath(root) : null;
        }
        catch (GitException)
        {
            return null;
        }
    }

    public async Task<GitStatus> GetStatusAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(repositoryRoot,
            ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--ignored=matching"],
            cancellationToken);
        return GitStatusParser.Parse(output, repositoryRoot);
    }

    /// <summary>Stage files (new, changed or deleted).</summary>
    public Task StageAsync(string repositoryRoot, IEnumerable<string> fullPaths)
        => RunAsync(repositoryRoot, ["add", "-A", "--", .. fullPaths]);

    /// <summary>Take files out of the index again, keeping the changes in the work tree.</summary>
    public async Task UnstageAsync(string repositoryRoot, IEnumerable<string> fullPaths)
    {
        var paths = fullPaths.ToList();
        if (await HasCommitsAsync(repositoryRoot))
            await RunAsync(repositoryRoot, ["restore", "--staged", "--", .. paths]);
        else
            await RunAsync(repositoryRoot, ["rm", "--cached", "-r", "-q", "--", .. paths]);   // nothing to restore from yet
    }

    /// <summary>Commit what is staged. With <paramref name="amend"/>, replace the last commit instead (an empty message keeps its message).</summary>
    public Task CommitAsync(string repositoryRoot, string message, bool amend = false)
        => RunAsync(repositoryRoot, amend
            ? message.Length == 0 ? ["commit", "-q", "--amend", "--no-edit"] : ["commit", "-q", "--amend", "-m", message]
            : ["commit", "-q", "-m", message]);

    /// <summary>
    /// The change as a patch, for describing it: with <paramref name="staged"/> what's staged, otherwise every change
    /// to tracked files since the last commit (staged or not). New files that aren't staged aren't in it.
    /// </summary>
    public async Task<string> GetDiffAsync(string repositoryRoot, bool staged)
    {
        List<string> arguments = ["diff", "--no-color", "--no-ext-diff", "-M"];
        if (staged)
            arguments.Add("--staged");
        else if (await HasCommitsAsync(repositoryRoot))
            arguments.Add("HEAD");
        return await RunAsync(repositoryRoot, arguments);
    }

    /// <summary>The first lines of the last few commit messages, newest first (empty before the first commit).</summary>
    public async Task<IReadOnlyList<string>> GetRecentSubjectsAsync(string repositoryRoot, int count)
    {
        if (!await HasCommitsAsync(repositoryRoot))
            return [];
        var output = await RunAsync(repositoryRoot, ["log", "-n", count.ToString(), "--format=%s"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------- undoing changes

    /// <summary>
    /// The Changes list's Undo Changes: put files back as they're staged (or as committed, when nothing is staged),
    /// throwing away the edits made since. Only for tracked files: a new file has nothing to go back to (see
    /// <see cref="DeleteUntrackedAsync"/>).
    /// </summary>
    public Task DiscardUnstagedAsync(string repositoryRoot, IEnumerable<string> fullPaths)
        => RunAsync(repositoryRoot, ["restore", "--worktree", "--", .. fullPaths]);

    /// <summary>
    /// The Staged list's Undo Changes (Visual Studio's): put files back as they are in the last commit, staged and
    /// not. A file the commit doesn't have (added) is deleted; a rename goes back to its old name, so pass both paths.
    /// </summary>
    public async Task UndoChangesAsync(string repositoryRoot, IEnumerable<string> fullPaths)
    {
        var paths = fullPaths.ToList();
        if (await HasCommitsAsync(repositoryRoot))
            await RunAsync(repositoryRoot, ["restore", "--source=HEAD", "--staged", "--worktree", "--", .. paths]);
        else
        {
            // Nothing committed to go back to: an added file is taken out of the index and deleted.
            await RunAsync(repositoryRoot, ["rm", "--cached", "-r", "-q", "--ignore-unmatch", "--", .. paths]);
            await DeleteUntrackedAsync(repositoryRoot, paths);
        }
    }

    /// <summary>Delete new files that git doesn't track (never ignored ones). Empty folders left behind go too.</summary>
    public Task DeleteUntrackedAsync(string repositoryRoot, IEnumerable<string> fullPaths)
        => RunAsync(repositoryRoot, ["clean", "-f", "-d", "-q", "--", .. fullPaths]);

    /// <summary>
    /// Discard All Changes: every tracked file back as in the last commit (staged changes too), and with
    /// <paramref name="deleteUntracked"/> every new file deleted (ignored files are kept).
    /// </summary>
    public async Task DiscardAllAsync(string repositoryRoot, bool deleteUntracked)
    {
        if (await HasCommitsAsync(repositoryRoot))
            await RunAsync(repositoryRoot, ["restore", "--source=HEAD", "--staged", "--worktree", "--", ":/"]);
        else
            await RunAsync(repositoryRoot, ["rm", "--cached", "-r", "-q", "--ignore-unmatch", "--", ":/"]);   // added files become new again
        if (deleteUntracked)
            await RunAsync(repositoryRoot, ["clean", "-f", "-d", "-q", "--", ":/"]);
    }

    /// <summary>
    /// Resolve conflicted files by taking one side whole (Visual Studio's Take Current / Take Incoming), then mark
    /// them resolved (staged).
    /// </summary>
    public async Task TakeConflictSideAsync(string repositoryRoot, IReadOnlyList<string> fullPaths, bool current)
    {
        await RunAsync(repositoryRoot, ["checkout", current ? "--ours" : "--theirs", "--", .. fullPaths]);
        await RunAsync(repositoryRoot, ["add", "--", .. fullPaths]);
    }

    /// <summary>
    /// VS Code's Undo Last Commit: take the last commit away, keeping its changes staged. The first commit leaves
    /// the branch with no commits again.
    /// </summary>
    public async Task UndoLastCommitAsync(string repositoryRoot)
    {
        var hasParent = await ResolvesAsync(repositoryRoot, "HEAD~1");
        if (hasParent)
            await RunAsync(repositoryRoot, ["reset", "--soft", "HEAD~1"]);
        else
            await RunAsync(repositoryRoot, ["update-ref", "-d", "HEAD"]);
    }

    /// <summary>
    /// Stop a merge, rebase, cherry-pick or revert that stopped on conflicts, putting things back as before it;
    /// returns what was aborted ("merge"), or null when none is in progress.
    /// </summary>
    public async Task<string?> AbortOperationAsync(string repositoryRoot)
    {
        var gitDirectory = (await RunAsync(repositoryRoot, ["rev-parse", "--absolute-git-dir"])).Trim();
        string[] command =
            Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")) ? ["rebase", "--abort"]
            : File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")) ? ["merge", "--abort"]
            : File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")) ? ["cherry-pick", "--abort"]
            : File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")) ? ["revert", "--abort"]
            : [];
        if (command.Length == 0)
            return null;
        await RunAsync(repositoryRoot, command);
        return command[0];
    }

    // ------------------------------------------------------------- remotes: pull, push, fetch

    /// <summary>Pull the current branch (merging or rebasing as the user's git config says).</summary>
    public Task PullAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["pull", "--no-edit"]);

    /// <summary>Pull, putting the branch's own commits on top of the remote's (VS Code's Pull (Rebase)).</summary>
    public Task PullRebaseAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["pull", "--rebase"]);

    /// <summary>
    /// Push the current branch. A branch with no upstream is published to <paramref name="remoteForNewBranch"/>
    /// (and tracks it from then on), as VS Code's "Publish Branch" does.
    /// </summary>
    public Task PushAsync(string repositoryRoot, bool hasUpstream, string? remoteForNewBranch)
        => hasUpstream || remoteForNewBranch is null
            ? RunAsync(repositoryRoot, ["push"])
            : RunAsync(repositoryRoot, ["push", "--set-upstream", remoteForNewBranch, "HEAD"]);

    public Task FetchAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["fetch", "--all", "--prune"]);

    public async Task<IReadOnlyList<string>> GetRemotesAsync(string repositoryRoot)
        => (await RunAsync(repositoryRoot, ["remote"])).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public Task AddRemoteAsync(string repositoryRoot, string name, string url)
        => RunAsync(repositoryRoot, ["remote", "add", name, url]);

    // ------------------------------------------------------------- branches and stashes

    /// <summary>Local branches, then remote ones ("origin/feature"); HEAD aliases are left out.</summary>
    public async Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryRoot)
    {
        var output = await RunAsync(repositoryRoot,
            ["for-each-ref", "--format=%(refname:short)%09%(symref)", "refs/heads", "refs/remotes"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length < 2 || fields[1].Length == 0)   // skip origin/HEAD -> origin/main
            .Select(fields => fields[0])
            .ToList();
    }

    /// <summary>Switch to a branch; a remote one ("origin/feature") gets a local branch that tracks it.</summary>
    public async Task SwitchBranchAsync(string repositoryRoot, string branch)
    {
        var remotes = await GetRemotesAsync(repositoryRoot);
        var isRemote = remotes.Any(remote => branch.StartsWith(remote + "/", StringComparison.Ordinal));
        await RunAsync(repositoryRoot, isRemote ? ["switch", "--track", branch] : ["switch", branch]);
    }

    /// <summary>Make a branch and switch to it: from HEAD, or from <paramref name="startPoint"/> (a commit or branch).</summary>
    public Task CreateBranchAsync(string repositoryRoot, string name, string? startPoint = null)
        => RunAsync(repositoryRoot, startPoint is null ? ["switch", "-c", name] : ["switch", "-c", name, startPoint]);

    /// <summary>Local branches only (for delete, rename and merge pickers).</summary>
    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repositoryRoot)
        => (await RunAsync(repositoryRoot, ["for-each-ref", "--format=%(refname:short)", "refs/heads"]))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Delete a local branch. Without <paramref name="force"/> git refuses one whose commits aren't merged anywhere
    /// (the message says so, and the caller can ask before forcing).
    /// </summary>
    public Task DeleteBranchAsync(string repositoryRoot, string name, bool force)
        => RunAsync(repositoryRoot, ["branch", force ? "-D" : "-d", name]);

    public Task RenameBranchAsync(string repositoryRoot, string oldName, string newName)
        => RunAsync(repositoryRoot, ["branch", "-m", oldName, newName]);

    /// <summary>Merge a branch into the current one (a merge commit with git's message when it can't fast-forward).</summary>
    public Task MergeAsync(string repositoryRoot, string branch) => RunAsync(repositoryRoot, ["merge", "--no-edit", branch]);

    /// <summary>Put all changes (new files too) aside, with a message when one is given.</summary>
    public Task StashAsync(string repositoryRoot, string? message = null)
        => RunAsync(repositoryRoot, message is { Length: > 0 }
            ? ["stash", "push", "--include-untracked", "-m", message]
            : ["stash", "push", "--include-untracked"]);

    /// <summary>Bring back the latest stash.</summary>
    public Task PopStashAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["stash", "pop"]);

    /// <summary>The stashes, newest first: "stash@{0}: On main: message".</summary>
    public async Task<IReadOnlyList<string>> GetStashesAsync(string repositoryRoot)
        => (await RunAsync(repositoryRoot, ["stash", "list", "--format=%gd: %gs"]))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Apply a stash ("stash@{1}", as <see cref="GetStashesAsync"/> starts each line): kept in the list, or with
    /// <paramref name="drop"/> removed from it once applied (pop).
    /// </summary>
    public Task ApplyStashAsync(string repositoryRoot, string stash, bool drop)
        => RunAsync(repositoryRoot, ["stash", drop ? "pop" : "apply", "--index", stash]);

    public Task DropStashAsync(string repositoryRoot, string stash) => RunAsync(repositoryRoot, ["stash", "drop", "-q", stash]);

    // ------------------------------------------------------------- commits (the graph's right-click menu)

    /// <summary>Check out a commit on its own (a detached HEAD), as VS Code's graph does.</summary>
    public Task CheckoutCommitAsync(string repositoryRoot, string commit) => RunAsync(repositoryRoot, ["switch", "--detach", commit]);

    /// <summary>Copy a commit onto the current branch.</summary>
    public Task CherryPickAsync(string repositoryRoot, string commit) => RunAsync(repositoryRoot, ["cherry-pick", commit]);

    /// <summary>A new commit on the current branch that undoes <paramref name="commit"/>, with git's message.</summary>
    public Task RevertCommitAsync(string repositoryRoot, string commit) => RunAsync(repositoryRoot, ["revert", "--no-edit", commit]);

    /// <summary>
    /// Move the current branch to <paramref name="commit"/>. Soft keeps the changes since staged, mixed keeps
    /// them unstaged, hard throws them away (and every uncommitted change).
    /// </summary>
    public Task ResetAsync(string repositoryRoot, string commit, GitResetMode mode)
        => RunAsync(repositoryRoot, ["reset", "-q", mode switch
        {
            GitResetMode.Soft => "--soft",
            GitResetMode.Hard => "--hard",
            _ => "--mixed"
        }, commit]);

    public Task CreateTagAsync(string repositoryRoot, string name, string commit) => RunAsync(repositoryRoot, ["tag", name, commit]);

    // ------------------------------------------------------------- history (the Git graph)

    /// <summary>
    /// The newest <paramref name="maxCount"/> commits, children before parents (<c>--topo-order</c>, so each branch's
    /// commits stay together): those on HEAD and <paramref name="upstream"/>, or with <paramref name="allBranches"/> on
    /// every branch, remote branch and tag. Empty before the first commit.
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string repositoryRoot, bool allBranches, string? upstream, int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (!await HasCommitsAsync(repositoryRoot))
            return [];

        List<string> arguments = ["log", "--topo-order", "--decorate=full", "-n", maxCount.ToString(), "--format=" + GitLogParser.Format];
        if (allBranches)
            arguments.AddRange(["--branches", "--remotes", "--tags"]);
        arguments.Add("HEAD");
        if (!allBranches && upstream is not null)
            arguments.Add(upstream);
        arguments.Add("--");   // the names above are revisions, not files
        return GitLogParser.ParseLog(await RunAsync(repositoryRoot, arguments, cancellationToken));
    }

    /// <summary>The files a commit changed, compared with its first parent (everything, for the first commit).</summary>
    public async Task<IReadOnlyList<GitCommitFile>> GetCommitFilesAsync(string repositoryRoot, GitCommit commit)
    {
        var output = await RunAsync(repositoryRoot, commit.Parents.Count == 0
            ? ["diff-tree", "--root", "--no-commit-id", "-r", "-M", "--name-status", "-z", commit.Hash]
            : ["diff-tree", "-r", "-M", "--name-status", "-z", commit.Parents[0], commit.Hash]);
        return GitLogParser.ParseNameStatus(output);
    }

    /// <summary>
    /// What the branches, tags and HEAD point at, as one string: it changes when a commit, fetch, pull, reset or branch
    /// switch moves any of them (the graph reloads only then).
    /// </summary>
    public async Task<string> GetRefsSnapshotAsync(string repositoryRoot)
    {
        try
        {
            var head = await RunAsync(repositoryRoot, ["rev-parse", "-q", "--verify", "HEAD"]);
            return head + await RunAsync(repositoryRoot, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/remotes", "refs/tags"]);
        }
        catch (GitException)
        {
            return "";   // no commits yet
        }
    }

    /// <summary>The file's text in a commit, or null if it isn't in it.</summary>
    public Task<string?> ReadAtCommitAsync(string repositoryRoot, string commit, string relativePath)
        => ReadObjectAsync(repositoryRoot, commit + ":" + relativePath);

    // ------------------------------------------------------------- .gitignore

    /// <summary>Ignore a file or folder: adds an anchored rule for it ("/path", "/folder/") to the root .gitignore.</summary>
    public async Task IgnoreAsync(string repositoryRoot, string fullPath)
        => await AppendIgnoreRuleAsync(repositoryRoot, "/" + RuleFor(repositoryRoot, fullPath));

    /// <summary>
    /// Stop ignoring a file or folder. If the rule that ignores it names exactly this path, that rule is removed;
    /// otherwise (a pattern such as "bin/" or "*.user") an exception "!/path" is added, so the pattern still covers
    /// everything else. Returns null when it worked, or why git still ignores it (a file inside an ignored folder
    /// can't be un-ignored on its own).
    /// </summary>
    public async Task<string?> UnignoreAsync(string repositoryRoot, string fullPath)
    {
        var rule = RuleFor(repositoryRoot, fullPath);
        if (await MatchingIgnoreRuleAsync(repositoryRoot, fullPath) is not var (source, line, pattern))
            return null;   // not ignored

        var sourcePath = Path.GetFullPath(Path.Combine(repositoryRoot, source));
        var exact = pattern.TrimStart('/').TrimEnd('/') == rule.TrimEnd('/')
                    && Path.GetDirectoryName(sourcePath) == Path.TrimEndingDirectorySeparator(repositoryRoot)
                    && File.Exists(sourcePath);
        if (exact)
        {
            // Keep the file's own line endings (WriteAllLines would use the OS's: \r\n on Windows).
            var text = await File.ReadAllTextAsync(sourcePath);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);   // the final newline, not a line
            if (line - 1 < lines.Count)
                lines.RemoveAt(line - 1);
            await File.WriteAllTextAsync(sourcePath, lines.Count == 0 ? "" : string.Join(newline, lines) + newline);
        }
        else
        {
            await AppendIgnoreRuleAsync(repositoryRoot, "!/" + rule);
        }

        return await MatchingIgnoreRuleAsync(repositoryRoot, fullPath) is var (_, _, still)
            ? $"git still ignores it (rule '{still}'): a file inside an ignored folder can't be included on its own."
            : null;
    }

    /// <summary>The rule that ignores a path (from git check-ignore -v), or null if it isn't ignored.</summary>
    private static async Task<(string Source, int Line, string Pattern)?> MatchingIgnoreRuleAsync(string repositoryRoot, string fullPath)
    {
        string output;
        try
        {
            output = await RunAsync(repositoryRoot, ["check-ignore", "-v", "--", fullPath]);
        }
        catch (GitException)
        {
            return null;   // exit code 1: not ignored
        }

        // <source>:<line>:<pattern>\t<path>   (the pattern may itself contain ':')
        var info = output.Split('\t')[0];
        var first = info.IndexOf(':');
        var second = info.IndexOf(':', first + 1);
        if (first < 0 || second < 0 || !int.TryParse(info[(first + 1)..second], out var line))
            return null;
        var pattern = info[(second + 1)..];
        return pattern.StartsWith('!') ? null : (info[..first], line, pattern);   // a "!" rule matched: it's included
    }

    private static async Task AppendIgnoreRuleAsync(string repositoryRoot, string rule)
    {
        var path = Path.Combine(repositoryRoot, ".gitignore");
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        await File.AppendAllTextAsync(path, separator + rule + "\n");
    }

    /// <summary>The path relative to the repository with '/' (and a trailing '/' for a folder), as rules write it.</summary>
    private static string RuleFor(string repositoryRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(repositoryRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return Directory.Exists(fullPath) ? relative.TrimEnd('/') + "/" : relative;
    }

    /// <summary>
    /// Who last changed each line of a file, with <paramref name="text"/> as its contents (the editor's, unsaved edits
    /// included: those lines come back as not committed). <c>git blame --porcelain --contents -</c>.
    /// </summary>
    public async Task<IReadOnlyList<GitBlameLine>> BlameAsync(string repositoryRoot, string relativePath, string text,
        CancellationToken cancellationToken = default)
        => GitBlame.Parse(await RunAsync(repositoryRoot, ["blame", "--porcelain", "--contents", "-", "--", relativePath],
            cancellationToken, text));

    /// <summary>The file's text in the last commit (HEAD), or null if it isn't in it (new file, or no commits yet).</summary>
    public Task<string?> ReadCommittedAsync(string repositoryRoot, string relativePath) => ReadObjectAsync(repositoryRoot, "HEAD:" + relativePath);

    /// <summary>The file's staged text (the index), or null if it isn't staged/tracked.</summary>
    public Task<string?> ReadStagedAsync(string repositoryRoot, string relativePath) => ReadObjectAsync(repositoryRoot, ":" + relativePath);

    private async Task<string?> ReadObjectAsync(string repositoryRoot, string spec)
    {
        try
        {
            return await RunAsync(repositoryRoot, ["show", spec]);
        }
        catch (GitException)
        {
            return null;
        }
    }

    private Task<bool> HasCommitsAsync(string repositoryRoot) => ResolvesAsync(repositoryRoot, "HEAD");

    /// <summary>Whether a revision ("HEAD", "HEAD~1") names a commit.</summary>
    private static async Task<bool> ResolvesAsync(string repositoryRoot, string revision)
    {
        try
        {
            await RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", revision + "^{commit}"]);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary>Run git in <paramref name="workingDirectory"/> and return its output; throws <see cref="GitException"/> on failure.</summary>
    private static async Task<string> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // never wait for input (a prompt would hang)
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";   // status won't take the index lock a running git needs
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";               // English messages, stable output

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new GitException($"Couldn't run git ({ex.Message}). Is git installed?");
        }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            }
            catch (IOException)
            {
                // git stopped reading (it failed): its exit code and message say why
            }
        }
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var message = (await error).Trim();
            throw new GitException(message.Length > 0 ? message : $"git {arguments[0]} failed (exit code {process.ExitCode}).");
        }
        return await output;
    }
}
