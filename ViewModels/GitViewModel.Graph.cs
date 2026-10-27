using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Git tab's Graph section (VS Code's Source Control Graph): the history as a graph, newest first, with branch
/// and tag labels. Clicking a commit lists the files it changed; clicking a file opens what the commit did to it.
///
/// It shows HEAD and its upstream, or every branch with <see cref="ShowAllBranches"/>, a page of commits at a time
/// ("Load More"). It's only read while the section is open, and re-read when a branch, tag or HEAD moves (a commit,
/// fetch, pull, switch... in the IDE or outside it), not on every save.
/// </summary>
public sealed partial class GitViewModel
{
    private const int GraphPageSize = 200;

    private int _graphCount = GraphPageSize;
    private int _graphVersion;
    private string? _graphSnapshot;   // what the shown graph was read from (GetRefsSnapshotAsync and the options)

    /// <summary>The graph's rows: <see cref="GitGraphCommitItem"/>s, the <see cref="GitGraphFileItem"/>s under an open one, and a <see cref="GitGraphLoadMoreItem"/>.</summary>
    public ObservableCollection<object> GraphItems { get; } = [];

    /// <summary>The Changes section is open (it shares the tab's height with the graph).</summary>
    [ObservableProperty]
    public partial bool IsChangesExpanded { get; set; } = true;

    /// <summary>The Graph section is open.</summary>
    [ObservableProperty]
    public partial bool IsGraphExpanded { get; set; } = true;

    /// <summary>Every branch, remote branch and tag, not just the current branch and its upstream.</summary>
    [ObservableProperty]
    public partial bool ShowAllBranches { get; set; }

    partial void OnIsGraphExpandedChanged(bool value)
    {
        if (value)
            _ = LoadGraphAsync();
    }

    partial void OnShowAllBranchesChanged(bool value)
    {
        _graphCount = GraphPageSize;
        _ = LoadGraphAsync();
    }

    /// <summary>View > Git Graph: show the Git tab with the Graph section open.</summary>
    [RelayCommand]
    private void ShowGraph()
    {
        _docking.ShowTool(ToolTab.Git);
        IsGraphExpanded = true;
    }

    [RelayCommand]
    private Task RefreshGraph()
    {
        _graphSnapshot = null;
        return LoadGraphAsync();
    }

    [RelayCommand]
    private Task LoadMoreCommits()
    {
        _graphCount += GraphPageSize;
        return LoadGraphAsync();
    }

    /// <summary>Clicking a commit: list the files it changed under it, or hide them again.</summary>
    [RelayCommand]
    private async Task ToggleCommitFiles(GitGraphCommitItem? item)
    {
        if (item is null)
            return;
        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            foreach (var file in item.Files ?? [])
                GraphItems.Remove(file);
            return;
        }

        item.IsExpanded = true;
        if (item.Files is null && _tracker.RepositoryRoot is { } root)
        {
            try
            {
                var files = await _git.GetCommitFilesAsync(root, item.Commit);
                item.Files = files.Select(file => new GitGraphFileItem(item, file)).ToList();
            }
            catch (GitException ex)
            {
                item.IsExpanded = false;
                Report(ex.Message);
                return;
            }
        }

        // Still open, and still in the graph (it may have been closed or reloaded while git ran)?
        var index = GraphItems.IndexOf(item);
        if (!item.IsExpanded || index < 0 || item.Files is null || GraphItems.ElementAtOrDefault(index + 1) is GitGraphFileItem)
            return;
        foreach (var file in item.Files)
            GraphItems.Insert(++index, file);
    }

    /// <summary>Clicking a file under a commit: what the commit changed in it, side by side.</summary>
    [RelayCommand]
    private void OpenCommitFile(GitGraphFileItem? item)
    {
        if (item is not null && _tracker.RepositoryRoot is { } root)
            _docking.OpenPage(new DiffDockable(item.Owner.Commit, item.File, root, _git, _documents));
    }

    [RelayCommand]
    private async Task CopyCommitHash(GitGraphCommitItem? item)
    {
        if (item is not null)
            await _clipboard.SetTextAsync(item.Commit.Hash);
    }

    [RelayCommand]
    private async Task CopyCommitMessage(GitGraphCommitItem? item)
    {
        if (item is not null)
            await _clipboard.SetTextAsync(item.Commit.Subject);
    }

    /// <summary>
    /// Read the graph again if anything it shows has moved (or it's forced by clearing <see cref="_graphSnapshot"/>).
    /// Opened commits stay open.
    /// </summary>
    private async Task LoadGraphAsync()
    {
        if (!IsGraphExpanded)
            return;
        var version = ++_graphVersion;
        if (_tracker.RepositoryRoot is not { } root)
        {
            GraphItems.Clear();
            _graphSnapshot = null;
            return;
        }

        var status = _tracker.Current;
        var snapshot = string.Join("\n", root, ShowAllBranches, _graphCount, status?.Upstream, await _git.GetRefsSnapshotAsync(root));
        if (version != _graphVersion || snapshot == _graphSnapshot)
            return;

        IReadOnlyList<GitCommit> commits;
        try
        {
            commits = await _git.GetLogAsync(root, ShowAllBranches, status?.Upstream, _graphCount);
        }
        catch (GitException ex)
        {
            Report(ex.Message);
            return;
        }
        if (version != _graphVersion)
            return;   // a newer load started meanwhile

        var open = GraphItems.OfType<GitGraphCommitItem>().Where(item => item.IsExpanded)
            .ToDictionary(item => item.Commit.Hash, item => item.Files);
        var rows = GitGraphLayout.Layout(commits);
        var items = commits.Select((commit, i) => new GitGraphCommitItem(commit, rows[i], commit.Hash == status?.HeadCommit)).ToList();

        GraphItems.Clear();
        foreach (var item in items)
            GraphItems.Add(item);
        if (commits.Count >= _graphCount)
            GraphItems.Add(new GitGraphLoadMoreItem());
        _graphSnapshot = snapshot;

        // Open again what was open, reusing the files already read when the commit is drawn the same way.
        foreach (var item in items.Where(item => open.ContainsKey(item.Commit.Hash)))
        {
            item.Files = open[item.Commit.Hash]?.Select(file => new GitGraphFileItem(item, file.File)).ToList();
            await ToggleCommitFiles(item);
        }
    }
}
