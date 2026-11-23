using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Undoing changes, always behind a warning (none of it can be undone): Visual Studio's Undo Changes on a file in
/// either list, VS Code's Discard All Changes on the Changes list's header, the "..." menu's Discard All Changes,
/// Undo Last Commit, and aborting a merge or rebase that stopped on conflicts.
/// </summary>
public sealed partial class GitViewModel
{
    /// <summary>
    /// A file's ↶ button and Undo Changes (right-click): in Changes, back to its staged version (or the last
    /// commit's); in Staged, back to the last commit's, staged and not. A new file is deleted.
    /// </summary>
    [RelayCommand]
    private Task UndoChanges(GitChangeItem? item)
        => item is null ? Task.CompletedTask : UndoAsync([.. Targets(item).Select(target => target.Change)], keepStaged: !item.IsStagedSide);

    /// <summary>The Changes header's ↶ button (VS Code's Discard All Changes): every unstaged change; staged ones stay.</summary>
    [RelayCommand]
    private Task UndoAllUnstaged()
        => Changes.Count == 0 ? Task.CompletedTask : UndoAsync([.. Changes.Select(item => item.Change)], keepStaged: true);

    /// <summary>The explorer's Git > Undo Changes: the file, or everything changed in the folder, back to the last commit.</summary>
    [RelayCommand]
    private Task UndoPath(string? fullPath)
        => fullPath is null ? Task.CompletedTask : UndoAsync([.. ChangesAtOrUnder(fullPath)], keepStaged: false);

    /// <summary>
    /// Warn (listing the files), then undo the changes: with <paramref name="keepStaged"/> only those since staging,
    /// otherwise everything since the last commit. New files are deleted, and the warning says so.
    /// </summary>
    private async Task UndoAsync(IReadOnlyList<GitChange> changes, bool keepStaged)
    {
        if (changes.Count == 0)
            return;
        var untracked = changes.Where(IsUntracked).ToList();
        var tracked = changes.Where(change => !IsUntracked(change)).ToList();
        // Back to the index only for files whose staged version is kept; a conflict has none to go back to.
        var toIndex = keepStaged ? tracked.Where(change => !change.IsConflicted).ToList() : [];
        var toHead = tracked.Except(toIndex).ToList();
        var added = toHead.Count(change => change.IndexState == GitFileState.Added);

        var what = changes.Count == 1 ? $"'{Path.GetFileName(changes[0].RelativePath)}'" : $"these {changes.Count} files";
        var message = untracked.Count == changes.Count
            ? $"Delete {what}? {(changes.Count == 1 ? "It's a new file" : "They're new files")}, so it's gone for good."
            : $"Undo the changes to {what}{(keepStaged && toHead.Count == 0 ? " since they were staged" : "")}?"
              + (untracked.Count + added > 0 ? $" {untracked.Count + added} new file{(untracked.Count + added == 1 ? " is" : "s are")} deleted." : "")
              + " This can't be undone.";
        var choice = untracked.Count == changes.Count ? (changes.Count == 1 ? "Delete File" : "Delete Files") : "Undo Changes";
        if (await _dialogs.WarnAsync("Undo Changes", message, [choice],
                changes.Count > 1 ? [.. changes.Select(change => change.FullPath)] : null) is null)
            return;

        await RunAsync(async root =>
        {
            if (toIndex.Count > 0)
                await _git.DiscardUnstagedAsync(root, toIndex.Select(change => change.FullPath));
            if (toHead.Count > 0)
                await _git.UndoChangesAsync(root, toHead.SelectMany(PathsOf));
            if (untracked.Count > 0)
                await _git.DeleteUntrackedAsync(root, untracked.Select(change => change.FullPath));
        }, null, changes.Count == 1 ? $"Undid the changes to {changes[0].RelativePath}." : $"Undid the changes to {changes.Count} files.");
    }

    private static bool IsUntracked(GitChange change) => change.WorkTreeState == GitFileState.Untracked;

    /// <summary>A rename goes back to its old name, so restore both.</summary>
    private IEnumerable<string> PathsOf(GitChange change)
        => change.OriginalRelativePath is { } original && _tracker.Current is { } status
            ? [change.FullPath, status.FullPathOf(original)]
            : [change.FullPath];

    /// <summary>
    /// "..." > Discard All Changes: every file back as in the last commit, staged changes too. New files are deleted
    /// only if the user picks that (VS Code's "Discard All N Files" against "Discard N Tracked Files").
    /// </summary>
    [RelayCommand]
    private async Task DiscardAll()
    {
        var changes = _tracker.Current?.Changes ?? [];
        if (changes.Count == 0)
        {
            _messenger.Send(new StatusMessage("Git: there are no changes to discard."));
            return;
        }
        var untracked = changes.Count(IsUntracked);
        var tracked = changes.Count - untracked;
        var all = $"Discard All {changes.Count} File{(changes.Count == 1 ? "" : "s")}";
        var trackedOnly = $"Discard {tracked} Tracked File{(tracked == 1 ? "" : "s")}";
        List<string> choices = untracked == 0 || tracked == 0 ? [all] : [trackedOnly, all];
        var message = $"Throw away every change in {changes.Count} file{(changes.Count == 1 ? "" : "s")}, staged or not, back to the last commit?"
                      + (untracked > 0 ? $" {untracked} of them {(untracked == 1 ? "is a new file" : "are new files")}: \"{all}\" deletes {(untracked == 1 ? "it" : "them")} too." : "")
                      + " This can't be undone.";

        var choice = await _dialogs.WarnAsync("Discard All Changes", message, choices, [.. changes.Select(change => change.FullPath)]);
        if (choice is null)
            return;
        var deleteUntracked = choice == all && untracked > 0;
        await RunAsync(root => _git.DiscardAllAsync(root, deleteUntracked), null, "Discarded all changes.");
    }

    /// <summary>VS Code's Undo Last Commit: the commit goes, its changes stay staged (to fix and commit again).</summary>
    [RelayCommand]
    private async Task UndoLastCommit()
    {
        if (_tracker.RepositoryRoot is not { } root || _tracker.Current is not { HeadCommit: not null } status)
        {
            _messenger.Send(new StatusMessage("Git: there's no commit to undo."));
            return;
        }
        string subject;
        try
        {
            subject = (await _git.GetRecentSubjectsAsync(root, 1)).FirstOrDefault() ?? "";
        }
        catch (GitException ex)
        {
            Report(ex.Message);
            return;
        }
        var pushed = status.Upstream is not null && status.Ahead == 0;
        var message = $"Undo the last commit, '{subject}'? Its changes are kept, staged."
                      + (pushed ? " It's already pushed: undoing it rewrites history others may have, and the next push needs forcing." : "");
        if (await _dialogs.WarnAsync("Undo Last Commit", message, ["Undo Commit"]) is null)
            return;
        if (await RunAsync(_git.UndoLastCommitAsync, null, $"Undid the commit '{subject}'; its changes are staged.") && CommitMessage.Length == 0)
            CommitMessage = subject;   // ready to commit again, as VS Code does
    }

    /// <summary>"..." > Abort Merge or Rebase: stop one that stopped on conflicts and put things back as before it.</summary>
    [RelayCommand]
    private async Task AbortOperation()
    {
        if (await _dialogs.WarnAsync("Abort Merge or Rebase",
                "Stop the merge, rebase, cherry-pick or revert in progress and put everything back as it was before it? " +
                "Conflicts you've resolved are lost.", ["Abort"]) is null)
            return;
        string? aborted = null;
        if (await RunAsync(async root => aborted = await _git.AbortOperationAsync(root)))
            _messenger.Send(new StatusMessage(aborted is null ? "Git: nothing is in progress to abort." : $"Git: aborted the {aborted}."));
    }
}
