using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Workspace;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The solution explorer: open a folder or a solution, refresh it, close it, and which node
/// is selected. Opening points the language servers at the new root.
/// </summary>
public sealed partial class WorkspaceViewModel(
    WorkspaceModel workspace,
    XamlLanguageService xaml,
    LanguageSessionService languages,
    IDialogService dialogs,
    IMessenger messenger) : ObservableObject
{
    private readonly WorkspaceModel _workspace = workspace;
    private readonly XamlLanguageService _xaml = xaml;
    private readonly LanguageSessionService _languages = languages;
    private readonly IDialogService _dialogs = dialogs;
    private readonly IMessenger _messenger = messenger;

    /// <summary>The tree roots (one: the folder or the solution).</summary>
    public ObservableCollection<ProjectTreeNode> Roots => _workspace.WorkspaceRoots;

    [ObservableProperty] public partial ProjectTreeNode? SelectedNode { get; set; }

    /// <summary>True while a folder or solution is open.</summary>
    public bool IsOpen => Roots.Count > 0;

    /// <summary>The open solution's .sln/.slnx, or null when a folder (or nothing) is open.</summary>
    public string? SolutionPath => Roots.FirstOrDefault(root => root.Kind == NodeKind.Solution)?.FullPath;

    /// <summary>A folder or solution was opened (or re-opened by a refresh). See WorkspaceSession.</summary>
    public event Action? Opened;

    /// <summary>The folder or solution was closed (Close Solution).</summary>
    public event Action? Closed;

    // ------------------------------------------------------------- open

    [RelayCommand]
    private async Task OpenFolder()
    {
        if (await _dialogs.PickFolderAsync() is not { } path) return;
        OpenFolder(path);
    }

    [RelayCommand]
    private async Task OpenSolution()
    {
        if (await _dialogs.PickSolutionAsync() is not { } path) return;
        try
        {
            OpenSolution(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                       or InvalidOperationException or ArgumentException)
        {
            _messenger.Send(new StatusMessage($"Couldn't open solution '{path}': {ex.Message}"));
        }
    }

    private void OpenFolder(string path)
    {
        _workspace.SolutionProjects = [];
        _xaml.ClearProjectCaches();
        SelectedNode = null;
        var root = ProjectTreeNode.CreateDirectory(path);
        root.Populate();
        Roots.Clear();
        Roots.Add(root);
        _languages.SetWorkspace(new LspWorkspace(path, SolutionPath: null));
        OnPropertyChanged(nameof(IsOpen));
        Opened?.Invoke();
    }

    private void OpenSolution(string path)
    {
        var root = SolutionLoader.LoadSolution(path, _workspace.Host);
        _xaml.ClearProjectCaches();
        SelectedNode = null;
        root.Populate();
        _workspace.SolutionProjects =
        [
            .. root.Children
                .Where(node => node.Kind == NodeKind.Project)
                .Select(node => node.FullPath)
        ];
        root.IsExpanded = true;
        Roots.Clear();
        Roots.Add(root);
        _languages.SetWorkspace(new LspWorkspace(Path.GetDirectoryName(path)!, SolutionPath: path));
        OnPropertyChanged(nameof(IsOpen));
        Opened?.Invoke();
    }

    // ---------------------------------------------------------- refresh

    /// <summary>Reload one node's children, or the whole workspace if it's the root.</summary>
    public async Task RefreshAsync(ProjectTreeNode node)
    {
        if (node.Kind is NodeKind.Directory or NodeKind.Solution
            && ReferenceEquals(node, Roots.FirstOrDefault()))
        {
            Reopen();
            return;
        }

        node.Reset();
        await node.PopulateAsync();
    }

    /// <summary>
    /// Reload the children of the folder or project that holds <paramref name="node"/> (after a file was renamed,
    /// pasted or deleted there), keeping the folders that were expanded expanded. Returns the node reloaded.
    /// </summary>
    public async Task<ProjectTreeNode?> ReloadAroundAsync(ProjectTreeNode node)
    {
        // A solution folder's children are fixed when its project is read, so reload the project (or real folder).
        var target = node;
        while (target is not { Kind: NodeKind.Directory or NodeKind.Project } && ParentOf(target) is { } parent)
            target = parent;
        if (target.Kind is not (NodeKind.Directory or NodeKind.Project))
            return null;

        var expanded = new HashSet<string>(GitStatus.PathComparer);
        CollectExpanded(target, expanded);

        target.Reset();
        await target.PopulateAsync();
        await ReExpandAsync(target, expanded);
        return target;
    }

    /// <summary>The node that lists <paramref name="node"/> as a child, or null (a root, or not in the tree).</summary>
    public ProjectTreeNode? ParentOf(ProjectTreeNode node)
        => Roots.Select(root => FindParent(root, node)).FirstOrDefault(parent => parent is not null);

    /// <summary>The loaded node for a path under <paramref name="under"/>, or null.</summary>
    public static ProjectTreeNode? FindByPath(ProjectTreeNode under, string path)
        => GitStatus.PathComparer.Equals(under.FullPath, path)
            ? under
            : under.Children.Select(child => FindByPath(child, path)).FirstOrDefault(found => found is not null);

    private static ProjectTreeNode? FindParent(ProjectTreeNode current, ProjectTreeNode target)
    {
        foreach (var child in current.Children)
        {
            if (ReferenceEquals(child, target))
                return current;
            if (FindParent(child, target) is { } found)
                return found;
        }
        return null;
    }

    private static void CollectExpanded(ProjectTreeNode node, HashSet<string> expanded)
    {
        foreach (var child in node.Children)
        {
            if (child.IsExpanded && child.FullPath.Length > 0)
                expanded.Add(child.FullPath);
            CollectExpanded(child, expanded);
        }
    }

    private static async Task ReExpandAsync(ProjectTreeNode node, HashSet<string> expanded)
    {
        foreach (var child in node.Children.ToList())
        {
            if (!expanded.Contains(child.FullPath))
                continue;
            await child.PopulateAsync();
            child.IsExpanded = true;
            await ReExpandAsync(child, expanded);
        }
    }

    private void Reopen()
    {
        if (Roots.FirstOrDefault() is not { } root)
            return;

        if (root.Kind == NodeKind.Solution)
            OpenSolution(root.FullPath);
        else
            OpenFolder(root.FullPath);
    }

    // ------------------------------------------------------------ close

    /// <summary>
    /// Forget the open folder/solution. Closing documents and stopping a debug session is the
    /// shell's job (MainWindowViewModel.CloseSolution), which calls this last.
    /// </summary>
    public void Close()
    {
        Roots.Clear();
        SelectedNode = null;
        _workspace.SolutionProjects = [];
        _xaml.ClearProjectCaches();
        _languages.ClearWorkspace();
        OnPropertyChanged(nameof(IsOpen));
        Closed?.Invoke();
    }
}
