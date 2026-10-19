using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>One row in the Git tab: a changed file, on the staged or the unstaged side.</summary>
public sealed record GitChangeItem(GitChange Change, bool IsStagedSide)
{
    public string FileName => Path.GetFileName(Change.RelativePath);

    /// <summary>The folder it's in, relative to the repository ("" at the root).</summary>
    public string Folder => Path.GetDirectoryName(Change.RelativePath)?.Replace('\\', '/') ?? "";

    /// <summary>The state on this side: what's staged, or what's changed since.</summary>
    public GitFileState State => Change.IsConflicted ? GitFileState.Conflicted
        : IsStagedSide ? Change.IndexState
        : Change.WorkTreeState;

    public string ToolTip => Change.OriginalRelativePath is { } original
        ? $"{Change.RelativePath} (renamed from {original})"
        : Change.RelativePath;
}

/// <summary>
/// The Git tab: the branch, the staged and unstaged changes, staging, and committing. The status itself
/// comes from <see cref="GitStatusTracker"/>, which keeps it current; this only shows it and acts on it.
/// </summary>
public sealed partial class GitViewModel : ObservableObject
{
    private readonly GitStatusTracker _tracker;
    private readonly GitService _git;
    private readonly IDocumentHost _documents;
    private readonly DockLayoutViewModel _docking;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;

    public GitViewModel(
        GitStatusTracker tracker,
        GitService git,
        IDocumentHost documents,
        DockLayoutViewModel docking,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _tracker = tracker;
        _git = git;
        _documents = documents;
        _docking = docking;
        _dialogs = dialogs;
        _messenger = messenger;
        tracker.Changed += OnStatusChanged;
    }

    public ObservableCollection<GitChangeItem> Staged { get; } = [];
    public ObservableCollection<GitChangeItem> Changes { get; } = [];

    public bool IsRepository => _tracker.RepositoryRoot is not null;

    /// <summary>"main", plus commits to push/pull ("main ↑2 ↓1"), or "detached HEAD".</summary>
    public string BranchText => _tracker.Current is not { } status ? ""
        : (status.Branch ?? "detached HEAD")
          + (status.Ahead > 0 ? $"  ↑{status.Ahead}" : "")
          + (status.Behind > 0 ? $"  ↓{status.Behind}" : "");

    public string StagedHeader => $"Staged Changes ({Staged.Count})";

    /// <summary>"Push", or "Publish Branch" when the branch isn't on a remote yet.</summary>
    public string PushHeader => _tracker.Current is { Upstream: null, Branch: not null } ? "Publish Branch" : "Push";
    public string ChangesHeader => $"Changes ({Changes.Count})";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitMessage { get; set; } = "";

    /// <summary>A git command is running (buttons wait for it).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial bool IsBusy { get; set; }

    private void OnStatusChanged()
    {
        var status = _tracker.Current;
        Replace(Staged, status?.Staged.Select(change => new GitChangeItem(change, IsStagedSide: true)));
        Replace(Changes, status?.Unstaged.Select(change => new GitChangeItem(change, IsStagedSide: false)));

        OnPropertyChanged(nameof(IsRepository));
        OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(StagedHeader));
        OnPropertyChanged(nameof(ChangesHeader));
        OnPropertyChanged(nameof(PushHeader));
        CommitCommand.NotifyCanExecuteChanged();
    }

    private static void Replace(ObservableCollection<GitChangeItem> list, IEnumerable<GitChangeItem>? items)
    {
        list.Clear();
        foreach (var item in items ?? [])
            list.Add(item);
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private Task Refresh() => _tracker.RefreshAsync();

    [RelayCommand]
    private Task Stage(GitChangeItem? item)
        => item is null ? Task.CompletedTask : RunAsync(root => _git.StageAsync(root, [item.Change.FullPath]));

    [RelayCommand]
    private Task Unstage(GitChangeItem? item)
        => item is null ? Task.CompletedTask : RunAsync(root => _git.UnstageAsync(root, [item.Change.FullPath]));

    [RelayCommand]
    private Task StageAll()
        => Changes.Count == 0 ? Task.CompletedTask
            : RunAsync(root => _git.StageAsync(root, Changes.Select(item => item.Change.FullPath).ToList()));

    [RelayCommand]
    private Task UnstageAll()
        => Staged.Count == 0 ? Task.CompletedTask
            : RunAsync(root => _git.UnstageAsync(root, Staged.Select(item => item.Change.FullPath).ToList()));

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        var message = CommitMessage.Trim();
        var count = Staged.Count;
        if (await RunAsync(root => _git.CommitAsync(root, message)))
        {
            CommitMessage = "";
            _messenger.Send(new StatusMessage($"Committed {count} file{(count == 1 ? "" : "s")}."));
        }
    }

    private bool CanCommit() => !IsBusy && Staged.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>Show the change side by side in a diff tab (double-click).</summary>
    [RelayCommand]
    private void OpenDiff(GitChangeItem? item)
    {
        if (item is not null && _tracker.RepositoryRoot is { } root)
            _docking.OpenDiff(new DiffDockable(item, root, _git, _tracker, _documents));
    }

    /// <summary>Open the changed file itself (a deleted file has nothing to open).</summary>
    [RelayCommand]
    private void OpenFile(GitChangeItem? item)
    {
        if (item is null || !File.Exists(item.Change.FullPath))
            return;
        try
        {
            _documents.OpenAndActivate(item.Change.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _messenger.Send(new StatusMessage($"Couldn't open '{item.Change.RelativePath}': {ex.Message}"));
        }
    }

    // ------------------------------------------------------------- the "..." menu

    [RelayCommand]
    private Task Pull() => RunAsync(root => _git.PullAsync(root), "Pulling...", "Pulled.");

    /// <summary>Push; a new branch is published (to the only remote, or one the user picks).</summary>
    [RelayCommand]
    private async Task Push()
    {
        if (_tracker.RepositoryRoot is not { } root || _tracker.Current is not { } status)
            return;
        string? remote = null;
        if (status.Upstream is null)
        {
            if (await PickRemoteAsync(root) is not { } picked)
                return;
            remote = picked;
        }
        await RunAsync(r => _git.PushAsync(r, hasUpstream: remote is null, remote), "Pushing...", "Pushed.");
    }

    [RelayCommand]
    private Task Fetch() => RunAsync(root => _git.FetchAsync(root), "Fetching...", "Fetched.");

    /// <summary>Pull, then push (VS Code's Sync).</summary>
    [RelayCommand]
    private async Task Sync()
    {
        if (await RunAsync(root => _git.PullAsync(root), "Pulling...", null))
            await Push();
    }

    /// <summary>Stage everything, then commit (when nothing was staged on purpose).</summary>
    [RelayCommand]
    private async Task CommitAll()
    {
        var message = CommitMessage.Trim();
        if (message.Length == 0)
        {
            _messenger.Send(new StatusMessage("Git: write a commit message first."));
            return;
        }
        if (await RunAsync(root => _git.StageAsync(root, ["."])) && await RunAsync(root => _git.CommitAsync(root, message)))
            CommitMessage = "";
    }

    /// <summary>Replace the last commit with the staged changes (and the message, if one is written).</summary>
    [RelayCommand]
    private async Task Amend()
    {
        if (!await _dialogs.ConfirmAsync("Amend Last Commit",
                "Replace the last commit with it plus the staged changes" +
                (CommitMessage.Trim().Length > 0 ? ", using the new message" : "") +
                "? Don't amend a commit that's already pushed.", "Amend"))
            return;
        var message = CommitMessage.Trim();
        if (await RunAsync(root => _git.CommitAsync(root, message, amend: true), null, "Amended the last commit."))
            CommitMessage = "";
    }

    [RelayCommand]
    private async Task DiscardAll()
    {
        if (await _dialogs.ConfirmAsync("Discard All Changes",
                "Throw away every change to tracked files, staged or not? New (untracked) files are kept. This can't be undone.",
                "Discard"))
            await RunAsync(root => _git.DiscardTrackedChangesAsync(root), null, "Discarded the changes.");
    }

    [RelayCommand]
    private async Task NewBranch()
    {
        if (await _dialogs.PromptForTextAsync("New Branch", "Branch name", "Create") is { } name && name.Trim().Length > 0)
            await RunAsync(root => _git.CreateBranchAsync(root, name.Trim()), null, $"Switched to the new branch {name.Trim()}.");
    }

    [RelayCommand]
    private async Task SwitchBranch()
    {
        if (_tracker.RepositoryRoot is not { } root)
            return;
        IReadOnlyList<string> branches;
        try
        {
            branches = await _git.GetBranchesAsync(root);
        }
        catch (GitException ex)
        {
            Report(ex.Message);
            return;
        }
        if (await _dialogs.PickAsync("Switch Branch", "Choose a branch (a remote one gets a local branch that tracks it).", branches) is { } branch)
            await RunAsync(r => _git.SwitchBranchAsync(r, branch), null, $"Switched to {branch}.");
    }

    [RelayCommand]
    private Task Stash() => RunAsync(root => _git.StashAsync(root), null, "Stashed the changes.");

    [RelayCommand]
    private Task PopStash() => RunAsync(root => _git.PopStashAsync(root), null, "Restored the latest stash.");

    /// <summary>Link the repository to a remote (e.g. a new GitHub repository's URL).</summary>
    [RelayCommand]
    private async Task AddRemote()
    {
        if (_tracker.RepositoryRoot is not { } root
            || await _dialogs.PromptForTextAsync("Add Remote", "Repository URL (https://... or git@...)", "Next") is not { } url
            || url.Trim().Length == 0)
            return;
        var remotes = await _git.GetRemotesAsync(root);
        var name = remotes.Contains("origin")
            ? await _dialogs.PromptForTextAsync("Add Remote", "Name for the remote", "Add")
            : "origin";
        if (name is { Length: > 0 })
            await RunAsync(r => _git.AddRemoteAsync(r, name.Trim(), url.Trim()), null, $"Added the remote {name.Trim()}.");
    }

    /// <summary>The remote to publish a new branch to: the only one, one the user picks, or null (none, or cancelled).</summary>
    private async Task<string?> PickRemoteAsync(string root)
    {
        var remotes = await _git.GetRemotesAsync(root);
        switch (remotes.Count)
        {
            case 0:
                Report("this repository has no remote yet: use ... > Add Remote first.");
                return null;
            case 1:
                return remotes[0];
            default:
                return await _dialogs.PickAsync("Publish Branch", "Push the branch to which remote?", remotes);
        }
    }

    // ------------------------------------------------------------- by path (the explorer's Git menu)

    /// <summary>Whether a file or folder is inside the open repository (and isn't its root).</summary>
    public bool Contains(string fullPath)
        => _tracker.RepositoryRoot is { } root
           && fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The changed file at the path, or null.</summary>
    public GitChange? ChangeFor(string fullPath) => _tracker.Current?.ChangeFor(fullPath);

    /// <summary>Whether the file, or anything inside the folder, has staged changes.</summary>
    public bool HasStagedChanges(string fullPath) => ChangesAtOrUnder(fullPath).Any(change => change.IsStaged);

    /// <summary>Whether the file, or anything inside the folder, can be staged.</summary>
    public bool HasUnstagedChanges(string fullPath) => ChangesAtOrUnder(fullPath).Any(change => change.HasUnstagedChanges);

    private IEnumerable<GitChange> ChangesAtOrUnder(string fullPath)
    {
        var folder = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
        return _tracker.Current?.Changes.Where(change =>
                   GitStatus.PathComparer.Equals(change.FullPath, fullPath)
                   || change.FullPath.StartsWith(folder, StringComparison.Ordinal))
               ?? [];
    }

    [RelayCommand]
    private Task StagePath(string? fullPath)
        => fullPath is null ? Task.CompletedTask : RunAsync(root => _git.StageAsync(root, [fullPath]));

    [RelayCommand]
    private Task UnstagePath(string? fullPath)
        => fullPath is null ? Task.CompletedTask : RunAsync(root => _git.UnstageAsync(root, [fullPath]));

    [RelayCommand]
    private Task Ignore(string? fullPath)
        => fullPath is null ? Task.CompletedTask : RunAsync(root => _git.IgnoreAsync(root, fullPath));

    [RelayCommand]
    private Task Unignore(string? fullPath)
        => fullPath is null ? Task.CompletedTask : RunAsync(async root =>
        {
            if (await _git.UnignoreAsync(root, fullPath) is { } problem)
                _messenger.Send(new StatusMessage($"{Path.GetFileName(fullPath)}: {problem}"));
        });

    /// <summary>The file's diff: its unstaged changes if it has any, else its staged ones.</summary>
    [RelayCommand]
    private void ShowDiff(string? fullPath)
    {
        if (fullPath is not null && ChangeFor(fullPath) is { } change)
            OpenDiff(new GitChangeItem(change, IsStagedSide: !change.HasUnstagedChanges));
    }

    private void Report(string gitMessage) => _messenger.Send(new StatusMessage("Git: " + Explain(gitMessage)));

    /// <summary>Git's error, or what to do about it when it's a sign-in problem (git can't prompt from here).</summary>
    private static string Explain(string message)
    {
        if (message.Contains("terminal prompts disabled", StringComparison.Ordinal)
            || message.Contains("could not read Username", StringComparison.Ordinal)
            || message.Contains("Authentication failed", StringComparison.Ordinal))
            return "the remote needs you to sign in, and git has no saved credentials. Set up a credential helper once in a terminal " +
                   "(e.g. git config --global credential.helper libsecret, from the git-credential-libsecret package), " +
                   "or use an SSH remote URL.";
        if (message.Contains("Permission denied (publickey)", StringComparison.Ordinal))
            return "the remote refused your SSH key. Check that your key is added to the remote (and to ssh-agent).";
        return message.Split('\n')[0];
    }

    /// <summary>
    /// Run a git command on the repository, then refresh. <paramref name="starting"/> and <paramref name="done"/>
    /// go to the status bar (for slow ones such as push); failures go there too, explained where possible.
    /// </summary>
    private async Task<bool> RunAsync(Func<string, Task> action, string? starting = null, string? done = null)
    {
        if (IsBusy || _tracker.RepositoryRoot is not { } root)
            return false;

        IsBusy = true;
        if (starting is not null)
            _messenger.Send(new StatusMessage("Git: " + starting));
        try
        {
            await action(root);
            if (done is not null)
                _messenger.Send(new StatusMessage("Git: " + done));
            return true;
        }
        catch (GitException ex)
        {
            Report(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)   // writing .gitignore
        {
            _messenger.Send(new StatusMessage("Git: " + ex.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
            await _tracker.RefreshAsync();
        }
    }
}
