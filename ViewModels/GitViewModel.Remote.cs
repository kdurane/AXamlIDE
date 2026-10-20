using System;
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
/// The Git tab's "..." menu: pull, push, fetch, sync, commit all / amend, discard, branches, stashes, remotes,
/// and setting up sign-in (Git Credential Manager) when a remote needs it.
/// </summary>
public sealed partial class GitViewModel
{
    // ------------------------------------------------------------- the "..." menu

    [RelayCommand]
    private Task Pull() => RunRemoteAsync(_git.PullAsync, "Pulling...", "Pulled.");

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
        await RunRemoteAsync(r => _git.PushAsync(r, hasUpstream: remote is null, remote), "Pushing...", "Pushed.");
    }

    [RelayCommand]
    private Task Fetch() => RunRemoteAsync(_git.FetchAsync, "Fetching...", "Fetched.");

    /// <summary>Pull, then push (VS Code's Sync).</summary>
    [RelayCommand]
    private async Task Sync()
    {
        if (await RunRemoteAsync(root => _git.PullAsync(root), "Pulling...", null))
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
    private Task Stash() => RunAsync(_git.StashAsync, null, "Stashed the changes.");

    [RelayCommand]
    private Task PopStash() => RunAsync(_git.PopStashAsync, null, "Restored the latest stash.");

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

    // ------------------------------------------------------------- signing in (Git Credential Manager)

    /// <summary>... > Remote > Set Up Sign-in: install and configure Git Credential Manager, after asking.</summary>
    [RelayCommand]
    private async Task SetUpSignIn()
    {
        if (await ConfirmSignInSetupAsync("Set Up Sign-in"))
            await SetUpCredentialManagerAsync();
    }

    /// <summary>
    /// A pull/push/fetch was refused for want of a sign-in. If git has no credential helper, offer to set up
    /// Git Credential Manager; true when it was set up (so the command can be tried again).
    /// </summary>
    private async Task<bool> OfferSignInSetupAsync()
    {
        if (_tracker.RepositoryRoot is not { } root || await _credentials.GetHelperAsync(root) is not null)
            return false;   // a helper is set up already: the problem is something else (account, token, rights)
        return await ConfirmSignInSetupAsync("Sign In to the Remote") && await SetUpCredentialManagerAsync();
    }

    private Task<bool> ConfirmSignInSetupAsync(string title) => _dialogs.ConfirmAsync(title,
        "Git needs to sign in to the remote (e.g. GitHub), and has nowhere to keep a sign-in yet. Set up Git Credential " +
        "Manager, as Visual Studio does? Then the first push or pull opens a sign-in window and your browser, and the " +
        "token is kept in your system keyring.\n\n" +
        "This will:\n" +
        "  - install it, if needed: dotnet tool install -g git-credential-manager\n" +
        "  - make it git's credential helper: git-credential-manager configure (your global git config)" +
        (OperatingSystem.IsLinux() ? "\n  - keep tokens in the desktop keyring: credential.credentialStore secretservice" : ""),
        "Set Up");

    private async Task<bool> SetUpCredentialManagerAsync()
    {
        var progress = new Progress<string>(text => _messenger.Send(new StatusMessage("Git: " + text)));
        if (await _credentials.SetUpAsync(progress) is { } problem)
        {
            _messenger.Send(new StatusMessage("Git: " + problem));
            return false;
        }
        _messenger.Send(new StatusMessage("Git: sign-in is set up (Git Credential Manager)."));
        return true;
    }
}
