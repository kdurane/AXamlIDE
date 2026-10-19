using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Gives every node in the explorer tree its <see cref="ProjectTreeNode.GitState"/> (the view colours it):
/// all nodes when the status changes, and new nodes as folders and projects load their children.
///
///   file              its own state (or Ignored if it's under an ignored folder)
///   folder / project  the strongest state of the changed files inside it (a project also counts its .csproj)
///   solution folder   the strongest state of its children (it isn't a real folder)
///   the root          nothing, or the whole tree's top line would always be coloured
/// </summary>
public sealed class GitTreeDecorator
{
    private readonly GitStatusTracker _git;
    private readonly WorkspaceModel _workspace;
    private readonly HashSet<ProjectTreeNode> _watched = [];

    public GitTreeDecorator(GitStatusTracker git, WorkspaceModel workspace)
    {
        _git = git;
        _workspace = workspace;
        git.Changed += DecorateAll;
        workspace.WorkspaceRoots.CollectionChanged += (_, _) => DecorateAll();
    }

    private void DecorateAll()
    {
        foreach (var node in _watched)
            node.Children.CollectionChanged -= OnChildrenChanged;
        _watched.Clear();

        foreach (var root in _workspace.WorkspaceRoots)
        {
            Decorate(root);
            root.GitState = GitFileState.None;
        }
    }

    /// <summary>Set the state of the node and everything loaded below it; returns the node's state.</summary>
    private GitFileState Decorate(ProjectTreeNode node)
    {
        if (_watched.Add(node))
            node.Children.CollectionChanged += OnChildrenChanged;

        var childStates = node.Children.Select(Decorate).ToList();   // children first: a solution folder needs them
        node.GitState = StateOf(node, childStates);
        return node.GitState;
    }

    private GitFileState StateOf(ProjectTreeNode node, List<GitFileState> childStates)
    {
        if (_git.Current is not { } status)
            return GitFileState.None;

        return node.Kind switch
        {
            NodeKind.File => status.StateOfFile(node.FullPath),
            NodeKind.Directory => status.StateOfFolder(node.FullPath),
            NodeKind.Project => GitStatus.Stronger(
                status.StateOfFile(node.FullPath),
                status.StateOfFolder(Path.GetDirectoryName(node.FullPath)!)),
            NodeKind.Folder => childStates.Aggregate(GitFileState.None, GitStatus.Stronger),
            _ => GitFileState.None
        };
    }

    // A folder or project loaded its children (or was refreshed): colour the new nodes.
    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var child in e.NewItems?.OfType<ProjectTreeNode>() ?? [])
            Decorate(child);
    }
}
