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
/// The "..." menu (remotes, branches, stashes, sign-in) is GitViewModel.Remote.cs; the explorer's Git
/// submenu (by path) is GitViewModel.Paths.cs.
/// </summary>
public sealed partial class GitViewModel : ObservableObject
{
    private readonly GitStatusTracker _tracker;
    private readonly GitService _git;
    private readonly GitCredentialSetup _credentials;
    private readonly IDocumentHost _documents;
    private readonly DockLayoutViewModel _docking;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;

    public GitViewModel(
        GitStatusTracker tracker,
        GitService git,
        GitCredentialSetup credentials,
        IDocumentHost documents,
        DockLayoutViewModel docking,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _tracker = tracker;
        _git = git;
        _credentials = credentials;
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

    /// <summary>The commit button (Visual Studio's): "Commit Staged" when something is staged, else "Commit All".</summary>
    public string CommitButtonText => Staged.Count > 0 ? "Commit Staged" : "Commit All";

    public string CommitButtonToolTip => Staged.Count > 0
        ? "Commit the staged changes (Ctrl+Enter)"
        : "Stage and commit all changes (Ctrl+Enter)";

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
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CommitButtonToolTip));
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
            : RunAsync(root => _git.StageAsync(root, [.. Changes.Select(item => item.Change.FullPath)]));

    [RelayCommand]
    private Task UnstageAll()
        => Staged.Count == 0 ? Task.CompletedTask
            : RunAsync(root => _git.UnstageAsync(root, [.. Staged.Select(item => item.Change.FullPath)]));

    /// <summary>The commit button and Ctrl+Enter: commit the staged changes, or everything when nothing is staged.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        if (Staged.Count == 0)
        {
            await CommitAll();
            return;
        }

        var message = CommitMessage.Trim();
        var count = Staged.Count;
        if (await RunAsync(root => _git.CommitAsync(root, message)))
        {
            CommitMessage = "";
            _messenger.Send(new StatusMessage($"Committed {count} file{(count == 1 ? "" : "s")}."));
        }
    }

    private bool CanCommit() => !IsBusy && Staged.Count + Changes.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>Show the change side by side in a diff tab (double-click).</summary>
    [RelayCommand]
    private void OpenDiff(GitChangeItem? item)
    {
        if (item is not null && _tracker.RepositoryRoot is { } root)
            _docking.OpenPage(new DiffDockable(item, root, _git, _tracker, _documents));
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

    // ------------------------------------------------------------- running git

    private void Report(string gitMessage) => _messenger.Send(new StatusMessage("Git: " + Explain(gitMessage)));

    private static bool IsSignInProblem(string message)
        => message.Contains("terminal prompts disabled", StringComparison.Ordinal)
           || message.Contains("could not read Username", StringComparison.Ordinal)
           || message.Contains("Authentication failed", StringComparison.Ordinal);

    /// <summary>Git's error, or what to do about it.</summary>
    private static string Explain(string message)
    {
        if (IsSignInProblem(message))
            return "the remote needs you to sign in. Use ... > Remote > Set Up Sign-in (Git Credential Manager), or an SSH remote URL.";
        if (message.Contains("Permission denied (publickey)", StringComparison.Ordinal))
            return "the remote refused your SSH key. Check that your key is added to the remote (and to ssh-agent).";
        return message.Split('\n')[0];
    }

    /// <summary>Run a git command on the repository, then refresh; true if it worked. Failures go to the status bar.</summary>
    private async Task<bool> RunAsync(Func<string, Task> action, string? starting = null, string? done = null)
    {
        var error = await TryRunAsync(action, starting, done);
        if (error is { Length: > 0 })
            Report(error);
        return error is null;
    }

    /// <summary>
    /// <see cref="RunAsync"/> for commands that talk to the remote: when it wants a sign-in that git can't ask
    /// for, offer to set up Git Credential Manager, then try once more.
    /// </summary>
    private async Task<bool> RunRemoteAsync(Func<string, Task> action, string starting, string? done)
    {
        var error = await TryRunAsync(action, starting, done);
        if (error is { Length: > 0 } && IsSignInProblem(error) && _tracker.RepositoryRoot is { } root)
        {
            if (await _credentials.FindBrokenHelperAsync(root) is { } broken)
                error = "Git Credential Manager won't start: " + broken;
            else if (await OfferSignInSetupAsync())
                error = await TryRunAsync(action, starting + " Sign in if a window or your browser opens.", done);
        }
        if (error is { Length: > 0 })
            Report(error);
        return error is null;
    }

    /// <summary>
    /// Run a git command on the repository, then refresh. <paramref name="starting"/> and <paramref name="done"/>
    /// go to the status bar (for slow ones such as push). Returns null when it worked, "" when it didn't run
    /// (busy, or no repository), else the error.
    /// </summary>
    private async Task<string?> TryRunAsync(Func<string, Task> action, string? starting, string? done)
    {
        if (IsBusy || _tracker.RepositoryRoot is not { } root)
            return "";

        IsBusy = true;
        if (starting is not null)
            _messenger.Send(new StatusMessage("Git: " + starting));
        try
        {
            await action(root);
            if (done is not null)
                _messenger.Send(new StatusMessage("Git: " + done));
            return null;
        }
        catch (GitException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)   // writing .gitignore
        {
            return ex.Message;
        }
        finally
        {
            IsBusy = false;
            await _tracker.RefreshAsync();
        }
    }
}
