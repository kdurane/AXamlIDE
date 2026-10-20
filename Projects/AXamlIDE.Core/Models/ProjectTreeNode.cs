using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Models;

public enum NodeKind { Directory, File, Solution, Project, Folder, Placeholder, Dependency }

/// <summary>
/// One node in either the plain filesystem tree (Open Folder) or the solution tree
/// (Open Solution). Both share this type - only how each node's children get
/// produced differs, via the populator delegate. Nodes with a populator get a
/// throwaway "Loading..." child so the expander arrow shows before anything has been
/// fetched; Populate/PopulateAsync swaps it for the real content, once.
/// </summary>
public class ProjectTreeNode : INotifyPropertyChanged
{
    private static readonly ProjectTreeNode LoadingPlaceholder =
        new("Loading...", string.Empty, NodeKind.Placeholder, null);

    public string Name { get; }
    public string FullPath { get; }
    public NodeKind Kind { get; }
    public ObservableCollection<ProjectTreeNode> Children { get; } = [];
    /// <summary>Bound two-way to the tree; raises PropertyChanged so a reload can re-expand folders.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    private bool _isExpanded;

    /// <summary>The node's Git state, for its colour in the explorer (set by ViewModels/GitTreeDecorator).</summary>
    public GitFileState GitState
    {
        get => _gitState;
        set
        {
            if (_gitState == value)
                return;
            _gitState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitState)));
        }
    }

    private GitFileState _gitState;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Icon => Kind switch
    {
        NodeKind.Directory => "Icon.FolderOutline",
        NodeKind.Solution => "Icon.Solution",
        NodeKind.Project => "Icon.FolderMultipleOutline",
        NodeKind.Folder => "Icon.FolderOutline",
        NodeKind.Placeholder => "Icon.DotsHorizontal",
        NodeKind.File => FileIcon(FullPath),
        NodeKind.Dependency => _dependencyIcon ?? "",
        _ => ""
    };

    /// <summary>The icon (path data) for a file by its extension; also used by Add New Item's template list.</summary>
    public static string FileIcon(string path) => path switch
    {
        _ when path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) => "Icon.FileCodeOutline",
        _ when path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "Icon.CodeJson",
        _ when path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) => "Icon.CSharp",
        _ when path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) => "Icon.FileDocumentOutline",
        _ when path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) => "Icon.CodeXml",
        _ => "Icon.FileOutline"
    };

    private string? _dependencyIcon;

    private readonly Func<IEnumerable<ProjectTreeNode>>? _populator;
    private bool _populated;
    public bool NeedsPopulating => _populator is not null && !_populated;

    public ProjectTreeNode(string name, string fullPath, NodeKind kind, Func<IEnumerable<ProjectTreeNode>>? populator)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        _populator = populator;

        if (populator is not null)
            Children.Add(LoadingPlaceholder);
    }

    /// <summary>Synchronous populate - fine for cheap populators (directories, the .sln parse).</summary>
    public void Populate()
    {
        if (_populated || _populator is null) return;
        _populated = true;

        Children.Clear();
        foreach (var child in _populator())
            Children.Add(child);
    }

    public void Reset()
    {
        _populated = false;
        Children.Clear();
        if (_populator is not null)
            Children.Add(LoadingPlaceholder);
    }

    /// <summary>
    /// Runs the populator on the thread pool (project evaluation goes through the MSBuild
    /// host and can take seconds), then fills Children back on the caller's (UI) context.
    /// </summary>
    public async Task PopulateAsync()
    {
        if (_populated || _populator is null) return;
        _populated = true;   // set first so a second Expanded event can't start another run

        try
        {
            var children = await Task.Run(() => _populator().ToList());

            Children.Clear();
            foreach (var child in children)
                Children.Add(child);
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new ProjectTreeNode($"Failed: {ex.Message}", string.Empty, NodeKind.Placeholder, null));
        }
    }

    // ---- Filesystem tree (Open Folder) ----

    public static ProjectTreeNode CreateDirectory(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return new ProjectTreeNode(string.IsNullOrEmpty(name) ? path : name, path, NodeKind.Directory,
            () => EnumerateDirectory(path));
    }

    private static IEnumerable<ProjectTreeNode> EnumerateDirectory(string path)
    {
        List<ProjectTreeNode> nodes;
        try
        {
            var dirs = Directory.EnumerateDirectories(path).OrderBy(d => d).Select(CreateDirectory);
            var files = Directory.EnumerateFiles(path).OrderBy(f => f)
                .Select(f => new ProjectTreeNode(Path.GetFileName(f), f, NodeKind.File, null));
            nodes = [.. dirs, .. GroupCodeBehindFiles(files)];
        }
        catch (UnauthorizedAccessException)
        {
            // Folder we can't read - leave it empty rather than blow up the tree.
            nodes = new List<ProjectTreeNode>();
        }
        return nodes;
    }

    public static IEnumerable<ProjectTreeNode> GroupCodeBehindFiles(IEnumerable<ProjectTreeNode> nodes)
    {
        var items = nodes.ToList();
        var files = items
            .Where(node => node.Kind == NodeKind.File && !string.IsNullOrEmpty(node.FullPath))
            .ToList();
        var byPath = files.ToDictionary(node => node.FullPath, StringComparer.OrdinalIgnoreCase);
        var codeBehindPaths = files
            .Where(node => node.FullPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Select(node => node.FullPath + ".cs")
            .Where(byPath.ContainsKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var node in items)
        {
            if (node.Kind == NodeKind.File && codeBehindPaths.Contains(node.FullPath))
                continue;

            if (node.Kind == NodeKind.File
                && node.FullPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                && byPath.TryGetValue(node.FullPath + ".cs", out var codeBehind))
                node.Children.Add(codeBehind);

            yield return node;
        }
    }

    // ---- Solution tree (Open Solution) ----

    public static ProjectTreeNode CreateSolution(string solutionPath, IReadOnlyList<(string Name, string Path)> projects, MsBuildHostClient host)
        => new(Path.GetFileNameWithoutExtension(solutionPath), solutionPath, NodeKind.Solution,
            () => projects.Select(p => CreateProject(p.Name, p.Path, host)));

    private static ProjectTreeNode CreateProject(string name, string csprojPath, MsBuildHostClient host)
        => new(name, csprojPath, NodeKind.Project, () => SolutionLoader.LoadProjectItems(host, csprojPath));

    /// <summary>An entry under a project's Dependencies node (see DependencyTree): a category, package, framework,
    /// project, analyzer or assembly. <paramref name="fullPath"/> is the file behind it when there is one, else "".</summary>
    public static ProjectTreeNode CreateDependency(string name, string icon, string fullPath, Func<IEnumerable<ProjectTreeNode>>? children)
        => new(name, fullPath, NodeKind.Dependency, children) { _dependencyIcon = icon };

    /// <summary>Folders under a project are fully known once the project has been evaluated
    /// (no further disk I/O), so this populates itself immediately. <paramref name="fullPath"/> is the
    /// folder on disk (for paste, rename, Copy Path).</summary>
    public static ProjectTreeNode CreateFolder(string name, string fullPath, IEnumerable<ProjectTreeNode> children)
    {
        var childList = children.ToList();
        var node = new ProjectTreeNode(name, fullPath, NodeKind.Folder, () => childList);
        node.Populate();
        return node;
    }
}
