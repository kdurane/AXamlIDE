using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
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
    private readonly IMessenger _messenger;

    public GitViewModel(
        GitStatusTracker tracker,
        GitService git,
        IDocumentHost documents,
        DockLayoutViewModel docking,
        IMessenger messenger)
    {
        _tracker = tracker;
        _git = git;
        _documents = documents;
        _docking = docking;
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

    /// <summary>Run a git command on the repository, then refresh. Failures go to the status bar.</summary>
    private async Task<bool> RunAsync(Func<string, Task> action)
    {
        if (IsBusy || _tracker.RepositoryRoot is not { } root)
            return false;

        IsBusy = true;
        try
        {
            await action(root);
            return true;
        }
        catch (GitException ex)
        {
            _messenger.Send(new StatusMessage("Git: " + ex.Message.Split('\n')[0]));
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
