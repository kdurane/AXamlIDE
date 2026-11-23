using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The "..." menu's Branch and Stash submenus: new, switch, merge, rename and delete branches (VS Code's Branch
/// menu), and stashing with a message, then applying, popping or dropping any stash (VS Code's Stash menu).
/// </summary>
public sealed partial class GitViewModel
{
    // ------------------------------------------------------------- branches

    [RelayCommand]
    private async Task NewBranch()
    {
        if (await _dialogs.PromptForTextAsync("New Branch", "Branch name", "Create") is { } name && name.Trim().Length > 0)
            await RunAsync(root => _git.CreateBranchAsync(root, name.Trim()), null, $"Switched to the new branch {name.Trim()}.");
    }

    [RelayCommand]
    private async Task SwitchBranch()
    {
        if (await ListAsync(_git.GetBranchesAsync) is not { } branches)
            return;
        if (await _dialogs.PickAsync("Switch Branch", "Choose a branch (a remote one gets a local branch that tracks it).", branches) is { } branch)
            await RunAsync(r => _git.SwitchBranchAsync(r, branch), null, $"Switched to {branch}.");
    }

    /// <summary>Merge another branch (local or remote) into the current one.</summary>
    [RelayCommand]
    private async Task MergeBranch()
    {
        if (await ListAsync(_git.GetBranchesAsync) is not { } branches)
            return;
        var current = _tracker.Current?.Branch;
        var others = branches.Where(branch => branch != current).ToList();
        if (await _dialogs.PickAsync("Merge Branch", $"Merge which branch into {current ?? "HEAD"}?", others) is { } branch)
            await RunAsync(r => _git.MergeAsync(r, branch), null, $"Merged {branch} into {current ?? "HEAD"}.");
    }

    /// <summary>Rename the current branch (VS Code's Rename Branch).</summary>
    [RelayCommand]
    private async Task RenameBranch()
    {
        if (_tracker.Current?.Branch is not { } current)
        {
            Report("there's no branch checked out to rename (HEAD is detached).");
            return;
        }
        if (await _dialogs.PromptForTextAsync("Rename Branch", "New name", "Rename", current) is { } name
            && name.Trim().Length > 0 && name.Trim() != current)
            await RunAsync(root => _git.RenameBranchAsync(root, current, name.Trim()), null, $"Renamed {current} to {name.Trim()}.");
    }

    /// <summary>
    /// Delete a local branch other than the current one. One whose commits aren't merged anywhere gets a warning
    /// first, as git refuses it without forcing.
    /// </summary>
    [RelayCommand]
    private async Task DeleteBranch()
    {
        if (await ListAsync(_git.GetLocalBranchesAsync) is not { } branches)
            return;
        var current = _tracker.Current?.Branch;
        var others = branches.Where(branch => branch != current).ToList();
        if (others.Count == 0)
        {
            Report("there's no other local branch to delete.");
            return;
        }
        if (await _dialogs.PickAsync("Delete Branch", "Delete which branch?", others) is not { } branch)
            return;

        var error = await TryRunAsync(root => _git.DeleteBranchAsync(root, branch, force: false), null, $"Deleted the branch {branch}.");
        if (error is { Length: > 0 } && error.Contains("not fully merged", StringComparison.Ordinal))
        {
            if (await _dialogs.WarnAsync("Delete Branch",
                    $"The branch '{branch}' has commits that aren't merged into any other branch. Delete it anyway? " +
                    "Those commits are lost.", ["Force Delete"]) is null)
                return;
            await RunAsync(root => _git.DeleteBranchAsync(root, branch, force: true), null, $"Deleted the branch {branch}.");
        }
        else if (error is { Length: > 0 })
            Report(error);
    }

    // ------------------------------------------------------------- stashes

    /// <summary>Put every change (new files too) aside, with an optional message to find it by.</summary>
    [RelayCommand]
    private async Task Stash()
    {
        if (await _dialogs.PromptForTextAsync("Stash Changes", "Message (optional)", "Stash") is { } message)
            await RunAsync(root => _git.StashAsync(root, message.Trim()), null, "Stashed the changes.");
    }

    [RelayCommand]
    private Task PopStash() => RunAsync(_git.PopStashAsync, null, "Restored the latest stash.");

    /// <summary>Apply a stash and keep it in the list.</summary>
    [RelayCommand]
    private async Task ApplyStash()
    {
        if (await PickStashAsync("Apply Stash", "Apply which stash? It stays in the list.") is { } stash)
            await RunAsync(root => _git.ApplyStashAsync(root, stash, drop: false), null, $"Applied {stash}.");
    }

    /// <summary>Apply a stash and take it off the list.</summary>
    [RelayCommand]
    private async Task PopStashPicked()
    {
        if (await PickStashAsync("Pop Stash", "Apply which stash, and remove it from the list?") is { } stash)
            await RunAsync(root => _git.ApplyStashAsync(root, stash, drop: true), null, $"Restored {stash}.");
    }

    [RelayCommand]
    private async Task DropStash()
    {
        if (await PickStashAsync("Drop Stash", "Throw away which stash?") is not { } stash)
            return;
        if (await _dialogs.WarnAsync("Drop Stash", $"Throw away {stash} and the changes in it? This can't be undone.", ["Drop"]) is not null)
            await RunAsync(root => _git.DropStashAsync(root, stash), null, $"Dropped {stash}.");
    }

    /// <summary>A stash the user picks, as "stash@{n}", or null (none, or cancelled).</summary>
    private async Task<string?> PickStashAsync(string title, string prompt)
    {
        if (await ListAsync(_git.GetStashesAsync) is not { } stashes)
            return null;
        if (stashes.Count == 0)
        {
            Report("there are no stashes.");
            return null;
        }
        var line = await _dialogs.PickAsync(title, prompt, stashes);
        return line?[..line.IndexOf(':')];   // "stash@{1}: On main: message"
    }

    /// <summary>A list read from git for a picker, or null (no repository, or git failed and said why).</summary>
    private async Task<IReadOnlyList<string>?> ListAsync(Func<string, Task<IReadOnlyList<string>>> read)
    {
        if (_tracker.RepositoryRoot is not { } root)
            return null;
        try
        {
            return await read(root);
        }
        catch (GitException ex)
        {
            Report(ex.Message);
            return null;
        }
    }
}
