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

    /// <summary>Throw away the changes to tracked files (staged and not). New files are left alone.</summary>
    public async Task DiscardTrackedChangesAsync(string repositoryRoot, IEnumerable<string>? fullPaths = null)
    {
        var paths = fullPaths?.ToList() is { Count: > 0 } list ? list : ["."];
        if (await HasCommitsAsync(repositoryRoot))
            await RunAsync(repositoryRoot, ["restore", "--staged", "--worktree", "--", .. paths]);
    }

    // ------------------------------------------------------------- remotes: pull, push, fetch

    /// <summary>Pull the current branch (merging or rebasing as the user's git config says).</summary>
    public Task PullAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["pull", "--no-edit"]);

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

    public Task CreateBranchAsync(string repositoryRoot, string name) => RunAsync(repositoryRoot, ["switch", "-c", name]);

    /// <summary>Put all changes (new files too) aside.</summary>
    public Task StashAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["stash", "push", "--include-untracked"]);

    /// <summary>Bring back the latest stash.</summary>
    public Task PopStashAsync(string repositoryRoot) => RunAsync(repositoryRoot, ["stash", "pop"]);

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
            var lines = (await File.ReadAllLinesAsync(sourcePath)).ToList();
            if (line - 1 < lines.Count)
                lines.RemoveAt(line - 1);
            await File.WriteAllLinesAsync(sourcePath, lines);
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

    private async Task<bool> HasCommitsAsync(string repositoryRoot)
    {
        try
        {
            await RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", "HEAD"]);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary>Run git in <paramref name="workingDirectory"/> and return its output; throws <see cref="GitException"/> on failure.</summary>
    private static async Task<string> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
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
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
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
