using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Agents;
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

    /// <summary>Stopped on a merge conflict: Take Current / Take Incoming / Mark as Resolved apply.</summary>
    public bool IsConflicted => Change.IsConflicted;

    public string ToolTip => Change.OriginalRelativePath is { } original
        ? $"{Change.RelativePath} (renamed from {original})"
        : Change.RelativePath;
}

/// <summary>
/// The Git tab: the branch, the staged and unstaged changes, staging, and committing. The status itself
/// comes from <see cref="GitStatusTracker"/>, which keeps it current; this only shows it and acts on it.
/// The "..." menu (remotes, branches, stashes, sign-in) is GitViewModel.Remote.cs; the explorer's Git
/// submenu (by path) is GitViewModel.Paths.cs; the Graph section is GitViewModel.Graph.cs.
/// </summary>
public sealed partial class GitViewModel : ObservableObject
{
    private readonly GitStatusTracker _tracker;
    private readonly GitService _git;
    private readonly GitCredentialSetup _credentials;
    private readonly IDocumentHost _documents;
    private readonly DockLayoutViewModel _docking;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IMessenger _messenger;
    private readonly CommitMessageWriter _messageWriter;

    public GitViewModel(
        GitStatusTracker tracker,
        GitService git,
        GitCredentialSetup credentials,
        IDocumentHost documents,
        DockLayoutViewModel docking,
        IDialogService dialogs,
        IClipboardService clipboard,
        CommitMessageWriter messageWriter,
        IMessenger messenger)
    {
        _tracker = tracker;
        _git = git;
        _credentials = credentials;
        _documents = documents;
        _docking = docking;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _messenger = messenger;
        _messageWriter = messageWriter;
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

    /// <summary>
    /// Nothing left to commit, but the branch has commits to send or fetch, or isn't on a remote yet: the commit
    /// button becomes Sync Changes / Publish Branch, as VS Code's does.
    /// </summary>
    private bool ShowsSync => Staged.Count + Changes.Count == 0
                              && _tracker.Current is { Branch: not null } status
                              && (status.Upstream is null || status.Ahead > 0 || status.Behind > 0);

    /// <summary>
    /// The commit button (Visual Studio's): "Commit Staged" when something is staged, else "Commit All"; once
    /// committed, "Sync Changes ↑1 ↓0" or "Publish Branch".
    /// </summary>
    public string CommitButtonText => ShowsSync
        ? _tracker.Current is { Upstream: null } ? "Publish Branch" : $"Sync Changes  ↑{_tracker.Current?.Ahead} ↓{_tracker.Current?.Behind}"
        : Staged.Count > 0 ? "Commit Staged" : "Commit All";

    public string CommitButtonToolTip => ShowsSync
        ? _tracker.Current is { Upstream: null } ? "Push the branch to a remote" : "Pull, then push the branch's commits"
        : Staged.Count > 0 ? "Commit the staged changes (Ctrl+Enter)"
        : "Stage and commit all changes (Ctrl+Enter)";

    /// <summary>The message box's hint, which says so while Claude is writing the message.</summary>
    public string CommitPlaceholder => IsGeneratingMessage ? "Claude is writing the commit message..." : "Commit message";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(PrimaryActionCommand))]
    public partial string CommitMessage { get; set; } = "";

    /// <summary>A git command is running (buttons wait for it).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(PrimaryActionCommand))]
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
        PrimaryActionCommand.NotifyCanExecuteChanged();
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        _ = LoadGraphAsync();
    }

    /// <summary>Show the new list, unless it's the same as before (so a refresh that changed nothing keeps the selection).</summary>
    private static void Replace(ObservableCollection<GitChangeItem> list, IEnumerable<GitChangeItem>? items)
    {
        var newItems = items?.ToList() ?? [];
        if (list.SequenceEqual(newItems))
            return;
        list.Clear();
        foreach (var item in newItems)
            list.Add(item);
    }

    // ------------------------------------------------------------- selection (several files at once)

    private IReadOnlyList<GitChangeItem> _selectedStaged = [];
    private IReadOnlyList<GitChangeItem> _selectedChanges = [];

    /// <summary>The view tells which rows are selected in a list (Ctrl+click and Shift+click pick several).</summary>
    public void SetSelection(bool stagedSide, IEnumerable<GitChangeItem> items)
    {
        if (stagedSide)
            _selectedStaged = [.. items];
        else
            _selectedChanges = [.. items];
    }

    /// <summary>
    /// What a row's button or menu acts on: every selected row on its side when it's one of them (as VS Code
    /// does), else just that row.
    /// </summary>
    private IReadOnlyList<GitChangeItem> Targets(GitChangeItem item)
    {
        var selected = item.IsStagedSide ? _selectedStaged : _selectedChanges;
        return selected.Contains(item) && selected.Count > 1 ? selected : [item];
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private Task Refresh() => _tracker.RefreshAsync();

    [RelayCommand]
    private Task Stage(GitChangeItem? item)
        => item is null ? Task.CompletedTask : RunAsync(root => _git.StageAsync(root, Targets(item).Select(target => target.Change.FullPath)));

    [RelayCommand]
    private Task Unstage(GitChangeItem? item)
        => item is null ? Task.CompletedTask : RunAsync(root => _git.UnstageAsync(root, Targets(item).Select(target => target.Change.FullPath)));

    // ------------------------------------------------------------- merge conflicts

    /// <summary>Visual Studio's Take Current: the conflicted files as the current branch has them, marked resolved.</summary>
    [RelayCommand]
    private Task TakeCurrent(GitChangeItem? item) => TakeSideAsync(item, current: true);

    /// <summary>Visual Studio's Take Incoming: the conflicted files as the branch being merged has them, marked resolved.</summary>
    [RelayCommand]
    private Task TakeIncoming(GitChangeItem? item) => TakeSideAsync(item, current: false);

    private async Task TakeSideAsync(GitChangeItem? item, bool current)
    {
        if (item is null)
            return;
        var files = Targets(item).Where(target => target.IsConflicted).Select(target => target.Change.FullPath).ToList();
        if (files.Count == 0)
            return;
        var side = current ? "current (ours)" : "incoming (theirs)";
        var what = files.Count == 1 ? $"'{Path.GetFileName(files[0])}'" : $"these {files.Count} files";
        if (await _dialogs.WarnAsync(current ? "Take Current" : "Take Incoming",
                $"Replace {what} with the {side} version, dropping the other side's changes and any merging you've done in it?",
                [current ? "Take Current" : "Take Incoming"], files.Count > 1 ? files : null) is null)
            return;
        await RunAsync(root => _git.TakeConflictSideAsync(root, files, current), null,
            $"Took the {side} version of {(files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files")}.");
    }

    /// <summary>Mark as Resolved: stage the conflicted files once their markers are sorted out (git add).</summary>
    [RelayCommand]
    private async Task MarkResolved(GitChangeItem? item)
    {
        if (item is null)
            return;
        var files = Targets(item).Where(target => target.IsConflicted).Select(target => target.Change.FullPath).ToList();
        var withMarkers = files.Where(HasConflictMarkers).ToList();
        if (withMarkers.Count > 0
            && await _dialogs.WarnAsync("Mark as Resolved",
                "Some files still have conflict markers (<<<<<<<) in them. Mark them resolved anyway?",
                ["Mark Resolved"], withMarkers) is null)
            return;
        await RunAsync(root => _git.StageAsync(root, files), null, "Marked as resolved.");
    }

    private static bool HasConflictMarkers(string path)
    {
        try
        {
            return File.Exists(path) && MergeConflicts.Find(File.ReadAllText(path)).Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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

    /// <summary>
    /// The big button under the message box: commit, or once there's nothing to commit, sync (or publish a new
    /// branch). Ctrl+Enter stays on <see cref="CommitCommand"/>, so the shortcut never pushes by surprise.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private Task PrimaryAction()
        => !ShowsSync ? Commit()
            : _tracker.Current is { Upstream: null } ? Push()   // Push publishes a branch that isn't on a remote
            : Sync();

    private bool CanPrimaryAction() => ShowsSync ? !IsBusy : CanCommit();

    /// <summary>Claude is writing the commit message (the sparkle button is disabled until it's done).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommitMessageCommand))]
    [NotifyPropertyChangedFor(nameof(CommitPlaceholder))]
    public partial bool IsGeneratingMessage { get; set; }

    /// <summary>
    /// The commit box's sparkle button (VS Code's "Generate Commit Message"): Claude describes what the commit button
    /// would commit (the staged changes, or everything) and the message box gets its answer, ready to edit.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateCommitMessage))]
    private async Task GenerateCommitMessage()
    {
        if (_tracker.RepositoryRoot is not { } root)
            return;

        var staged = Staged.Count > 0;
        // Commit All adds new files too, which git diff doesn't show: name them.
        var newFiles = staged ? [] : Changes.Where(item => item.State == GitFileState.Untracked).Select(item => item.Change.RelativePath).ToList();
        IsGeneratingMessage = true;
        _messenger.Send(new StatusMessage("Claude is writing the commit message..."));
        try
        {
            CommitMessage = await _messageWriter.WriteAsync(root, staged, newFiles);
            _messenger.Send(new StatusMessage("Commit message written by Claude: check it before committing."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or GitException)
        {
            _messenger.Send(new StatusMessage(ex.Message));
        }
        finally
        {
            IsGeneratingMessage = false;
        }
    }

    private bool CanGenerateCommitMessage() => !IsGeneratingMessage && Staged.Count + Changes.Count > 0;

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
