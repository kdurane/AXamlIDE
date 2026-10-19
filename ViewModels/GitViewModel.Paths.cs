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

/// <summary>Git by file or folder path: the explorer's right-click Git submenu.</summary>
public sealed partial class GitViewModel
{
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
}
