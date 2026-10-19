using System;
using System.IO;
using AXamlIDE.Models;

namespace AXamlIDE.ViewModels;

/// <summary>
/// What the explorer's right-click menu (ExplorerView.axaml) shows for one node: which items are visible, and
/// the commands with their parameter. Made fresh each time the menu opens, for the node that was clicked.
/// </summary>
public sealed class ExplorerNodeMenu
{
    public ExplorerNodeMenu(ProjectTreeNode node, ExplorerViewModel explorer)
    {
        Node = node;
        Explorer = explorer;

        IsFile = node.Kind == NodeKind.File;
        CanChange = explorer.CanChange(node);
        HasPath = !string.IsNullOrEmpty(node.FullPath);
        CanRefresh = node.Kind is NodeKind.Directory or NodeKind.Solution or NodeKind.Project or NodeKind.Folder;
        // A project node in a solution; in an opened folder, projects are plain .csproj files.
        IsProject = NuGetManager.IsProject(node);
        CanBuild = IsProject
                   || IsFile && node.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        // Git acts on the file or folder; a project acts on its folder.
        GitPath = node.Kind switch
        {
            NodeKind.File or NodeKind.Directory or NodeKind.Folder => node.FullPath,
            NodeKind.Project => Path.GetDirectoryName(node.FullPath) ?? "",
            _ => ""
        };
        var git = explorer.Git;
        var inRepository = GitPath.Length > 0 && git.Contains(GitPath);
        CanShowDiff = inRepository && IsFile && git.ChangeFor(GitPath) is not null;
        CanStage = inRepository && git.HasUnstagedChanges(GitPath);
        CanUnstage = inRepository && git.HasStagedChanges(GitPath);
        var ignorable = inRepository && node.Kind is NodeKind.File or NodeKind.Directory or NodeKind.Folder;
        CanUnignore = ignorable && node.GitState == GitFileState.Ignored;
        CanIgnore = ignorable && !CanUnignore;
        ShowGit = CanShowDiff || CanStage || CanUnstage || CanIgnore || CanUnignore;
    }

    public ProjectTreeNode Node { get; }
    public ExplorerViewModel Explorer { get; }

    public bool IsFile { get; }
    public bool CanChange { get; }
    public bool HasPath { get; }
    public bool CanRefresh { get; }
    public bool IsProject { get; }
    public bool CanBuild { get; }

    /// <summary>The path the Git items act on (the parameter of their commands).</summary>
    public string GitPath { get; }

    public bool ShowGit { get; }
    public bool CanShowDiff { get; }
    public bool CanStage { get; }
    public bool CanUnstage { get; }
    public bool CanIgnore { get; }
    public bool CanUnignore { get; }

    /// <summary>The separator between Diff/Add/Unstage and the .gitignore item.</summary>
    public bool ShowGitSeparator => (CanShowDiff || CanStage || CanUnstage) && (CanIgnore || CanUnignore);
}
