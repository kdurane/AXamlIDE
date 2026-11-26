using AXamlIDE.Services.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Workspace;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The solution explorer: open a folder or a solution, refresh it, close it, and which node
/// is selected. Opening points the language servers at the new root. Files added, removed or renamed on disk by
/// anything (Claude, a terminal, another editor) show up by themselves, as in VS Code and Rider.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IRecipient<FilesCreatedMessage>
{
    private readonly WorkspaceModel _workspace;
    private readonly XamlLanguageService _xaml;
    private readonly LanguageSessionService _languages;
    private readonly IMessenger _messenger;
    private bool _opening;   // a solution is loading (OpenAsync)

    // Paths added, removed or renamed on disk, gathered for a moment (a git checkout or a code generator writes
    // many at once) and then put in the tree together.
    private readonly HashSet<string> _diskChanges = new(GitStatus.PathComparer);
    private readonly DispatcherTimer _diskTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _applyingDiskChanges;

    public WorkspaceViewModel(WorkspaceModel workspace, XamlLanguageService xaml, LanguageSessionService languages,
        IMessenger messenger, GitStatusTracker git)
    {
        _workspace = workspace;
        _xaml = xaml;
        _languages = languages;
        _messenger = messenger;
        messenger.RegisterAll(this);
        git.FileAddedOrRemoved += OnFileAddedOrRemoved;
        _diskTimer.Tick += OnDiskTimerTick;
    }

    /// <summary>The tree roots (one: the folder or the solution).</summary>
    public ObservableCollection<ProjectTreeNode> Roots => _workspace.WorkspaceRoots;

    [ObservableProperty] public partial ProjectTreeNode? SelectedNode { get; set; }

    /// <summary>True while a folder or solution is open.</summary>
    public bool IsOpen => Roots.Count > 0;

    /// <summary>The open solution's .sln/.slnx, or null when a folder (or nothing) is open.</summary>
    public string? SolutionPath => Roots.FirstOrDefault(root => root.Kind == NodeKind.Solution)?.FullPath;

    /// <summary>What was opened: the solution file, or the folder (null when nothing is open).</summary>
    public string? WorkspacePath => SolutionPath ?? Roots.FirstOrDefault()?.FullPath;

    /// <summary>A folder or solution was opened (or re-opened by a refresh). See WorkspaceSession.</summary>
    public event Action? Opened;

    /// <summary>The folder or solution was closed (Close Solution).</summary>
    public event Action? Closed;

    // ------------------------------------------------------------- open

    /// <summary>
    /// Open a solution (.sln/.slnx) or a folder, replacing what's open (the caller deals with unsaved changes first).
    /// False, with the reason in the status bar, when it can't be opened. A solution is loaded off the UI thread
    /// (every project is evaluated by the MSBuild host, which can take seconds), so windows keep drawing meanwhile.
    /// </summary>
    public async Task<bool> OpenAsync(string path)
    {
        if (_opening)
            return false;   // one at a time: a second click while a solution loads is ignored
        _opening = true;
        PerfTimeline.Begin(PerfTimeline.Open, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        try
        {
            if (RecentWorkspace.IsSolutionPath(path) && File.Exists(path))
                await OpenSolutionAsync(path);
            else if (Directory.Exists(path))
                OpenFolder(path);
            else
            {
                _messenger.Send(new StatusMessage($"'{path}' no longer exists."));
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                       or InvalidOperationException or ArgumentException)
        {
            _messenger.Send(new StatusMessage($"Couldn't open '{path}': {ex.Message}"));
            return false;
        }
        finally
        {
            _opening = false;
        }
    }

    private void OpenFolder(string path)
    {
        _workspace.SolutionProjects = [];
        _workspace.UseFolder(path);
        _workspace.ClearProjectCaches();
        _xaml.ClearProjectCaches();
        SelectedNode = null;
        var root = ProjectTreeNode.CreateDirectory(path);
        root.Populate();
        Roots.Clear();
        Roots.Add(root);
        PerfTimeline.Mark(PerfTimeline.Open, "tree-shown");
        _languages.SetWorkspace(new LspWorkspace(path, SolutionPath: null, _workspace.ProjectFilesUnderRoot));
        OnPropertyChanged(nameof(IsOpen));
        Opened?.Invoke();
    }

    private async Task OpenSolutionAsync(string path)
    {
        _workspace.UseFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);   // its global.json picks the SDK
        _workspace.ClearProjectCaches();   // before loading: the loader evaluates every project
        _messenger.Send(new StatusMessage($"Opening {Path.GetFileName(path)}..."));
        // Off the UI thread: the tree isn't shown until it's in Roots, so filling it there is safe.
        var root = await Task.Run(() =>
        {
            var loaded = SolutionLoader.LoadSolution(path, _workspace.Host);
            loaded.Populate();
            return loaded;
        });
        PerfTimeline.Mark(PerfTimeline.Open, "projects-read",
            $"{root.Children.Count(node => node.Kind == NodeKind.Project)} projects");
        _xaml.ClearProjectCaches();
        SelectedNode = null;
        _workspace.SolutionProjects =
        [
            .. root.Children
                .Where(node => node.Kind == NodeKind.Project)
                .Select(node => node.FullPath)
        ];
        root.IsExpanded = true;
        Roots.Clear();
        Roots.Add(root);
        PerfTimeline.Mark(PerfTimeline.Open, "tree-shown");
        _languages.SetWorkspace(new LspWorkspace(Path.GetDirectoryName(path)!, SolutionPath: path));
        OnPropertyChanged(nameof(IsOpen));
        _messenger.Send(new StatusMessage(""));
        Opened?.Invoke();
    }

    // ---------------------------------------------------------- refresh

    /// <summary>
    /// Reload one node's children from disk, or the whole workspace if it's the root. A project folder's children come
    /// from the project's evaluation, so its project is evaluated again.
    /// </summary>
    public async Task RefreshAsync(ProjectTreeNode node)
    {
        if (node.Kind is NodeKind.Directory or NodeKind.Solution
            && ReferenceEquals(node, Roots.FirstOrDefault()))
        {
            await ReopenAsync();
            return;
        }

        if (await ReloadAroundAsync(node) is null)
        {
            node.Reset();   // a dependency: re-read just it
            await node.PopulateAsync();
        }
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
        // A selected node under it is about to be replaced: select its replacement after (nothing, if it's gone).
        var selected = SelectedNode is { FullPath.Length: > 0 } old && !ReferenceEquals(old, target)
                                                                    && FindByPath(target, old.FullPath) is not null
            ? old
            : null;

        // Its files come from the project's evaluation, which doesn't know about files added since.
        if (target.Kind == NodeKind.Project)
            _workspace.ForgetProject(target.FullPath);
        target.Reset();
        await target.PopulateAsync();
        await ReExpandAsync(target, expanded);
        if (selected is not null && (SelectedNode is null || ReferenceEquals(SelectedNode, selected)))
            SelectedNode = FindByPath(target, selected.FullPath);
        return target;
    }

    /// <summary>
    /// The node for a file or folder, loading and expanding the folders down to it (Visual Studio's Sync with Active
    /// Document). Null when the path isn't in the workspace.
    /// </summary>
    public async Task<ProjectTreeNode?> FindOrLoadAsync(string path)
    {
        foreach (var root in Roots.ToList())
            if (await FindOrLoadAsync(root, path) is { } found)
                return found;
        return null;
    }

    private static async Task<ProjectTreeNode?> FindOrLoadAsync(ProjectTreeNode node, string path)
    {
        if (node.Kind == NodeKind.Placeholder)
            return null;
        if (node.FullPath.Length > 0 && GitStatus.PathComparer.Equals(node.FullPath, path))
            return node;
        // A real folder: only worth descending when the path is inside it. A project, a solution or a virtual
        // folder lists files from anywhere, so look through all of them.
        if (node.Kind is NodeKind.Directory && !IsUnder(path, node.FullPath))
            return null;
        if (node.Kind == NodeKind.File)
            return null;

        if (node.NeedsPopulating)
            await node.PopulateAsync();
        foreach (var child in node.Children.ToList())
        {
            if (await FindOrLoadAsync(child, path) is not { } found)
                continue;
            node.IsExpanded = true;
            return found;
        }
        return null;
    }

    private static bool IsUnder(string path, string folder)
        => folder.Length > 0
           && path.Length > folder.Length
           && path.StartsWith(folder, GitStatus.PathComparer.Equals("a", "A") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
           && (folder.EndsWith(Path.DirectorySeparatorChar) || path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar);

    /// <summary>Collapse every folder (the explorer's Collapse All), leaving the root open.</summary>
    public void CollapseAll()
    {
        foreach (var root in Roots)
        {
            foreach (var child in root.Children)
                Collapse(child);
        }

        static void Collapse(ProjectTreeNode node)
        {
            node.IsExpanded = false;
            foreach (var child in node.Children)
                Collapse(child);
        }
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

    /// <summary>The paths of every expanded node, for the session to save.</summary>
    public List<string> ExpandedPaths()
    {
        var expanded = new HashSet<string>(GitStatus.PathComparer);
        foreach (var root in Roots)
        {
            if (root.IsExpanded && root.FullPath.Length > 0)
                expanded.Add(root.FullPath);
            CollectExpanded(root, expanded);
        }
        return [.. expanded];
    }

    /// <summary>Expand these again in a reopened workspace, loading each node on the way down.</summary>
    public async Task RestoreExpandedAsync(IEnumerable<string> paths)
    {
        var expanded = new HashSet<string>(paths, GitStatus.PathComparer);
        foreach (var root in Roots.ToList())
        {
            if (expanded.Contains(root.FullPath))
                root.IsExpanded = true;
            await ReExpandAsync(root, expanded);
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

    private async Task ReopenAsync()
    {
        if (Roots.FirstOrDefault() is not { } root)
            return;

        if (root.Kind == NodeKind.Solution)
            await OpenSolutionAsync(root.FullPath);
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
        _workspace.ClearProjectCaches();
        _xaml.ClearProjectCaches();
        _languages.ClearWorkspace();
        OnPropertyChanged(nameof(IsOpen));
        Closed?.Invoke();
    }

    /// <summary>A code action (or anything else) created files: reload the folders or projects that hold them.</summary>
    public async void Receive(FilesCreatedMessage message)
    {
        var containers = message.Paths
         .Select(FindContainer)
         .OfType<ProjectTreeNode>()
         .Distinct();
        foreach (var container in containers)
            await ReloadAroundAsync(container);
    }

    // ------------------------------------------------------- following the disk

    private void OnFileAddedOrRemoved(string path)
    {
        if (!IsOpen)
            return;
        _diskChanges.Add(path);
        _diskTimer.Stop();   // restart: wait until the burst is over
        _diskTimer.Start();
    }

    private async void OnDiskTimerTick(object? sender, EventArgs e)
    {
        _diskTimer.Stop();
        if (_applyingDiskChanges)
        {
            _diskTimer.Start();   // still putting the last lot in: these go in after
            return;
        }
        var paths = _diskChanges.ToList();
        _diskChanges.Clear();
        _applyingDiskChanges = true;
        try
        {
            await ApplyDiskChangesAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // a folder that can't be read: the tree catches up on the next change or a Refresh
        }
        finally
        {
            _applyingDiskChanges = false;
        }
    }

    /// <summary>
    /// Files or folders appeared, went or were renamed on disk: reload the loaded folders and projects that show (or
    /// should show) them. A project's evaluation is forgotten either way, so it's read afresh when next asked about,
    /// even one that isn't expanded yet. A folder that hasn't been opened reads the disk when it is, so it's left alone.
    /// </summary>
    public async Task ApplyDiskChangesAsync(IEnumerable<string> paths)
    {
        var containers = new List<ProjectTreeNode>();
        foreach (var path in paths)
        {
            if (_workspace.FindProjectForDocument(path) is { } project)
                _workspace.ForgetProject(project);

            var onDisk = File.Exists(path) || Directory.Exists(path);
            var inTree = Roots.Any(root => FindByPath(root, path) is not null);
            if (onDisk == inTree)
                continue;   // the tree already shows it as it is (one the IDE added itself, say)
            if (FindContainer(path) is { NeedsPopulating: false } container && !containers.Contains(container))
                containers.Add(container);
        }
        foreach (var container in containers)
            await ReloadAroundAsync(container);
    }

    private ProjectTreeNode? FindContainer(string path)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var node in Roots.SelectMany(Descendants))
            {
                if (node.Kind == NodeKind.Directory && GitStatus.PathComparer.Equals(node.FullPath, dir))
                    return node;
                if (node.Kind == NodeKind.Project && GitStatus.PathComparer.Equals(Path.GetDirectoryName(node.FullPath), dir))
                    return node;
            }
        }
        return null;
    }

    /// <summary>The node and every loaded node under it.</summary>
    private static IEnumerable<ProjectTreeNode> Descendants(ProjectTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

}
