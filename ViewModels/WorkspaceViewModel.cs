using System;
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
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly WorkspaceModel _workspace;
    private readonly XamlLanguageService _xaml;
    private readonly LanguageSessionService _languages;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;

    public WorkspaceViewModel(
        WorkspaceModel workspace,
        XamlLanguageService xaml,
        LanguageSessionService languages,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _workspace = workspace;
        _xaml = xaml;
        _languages = languages;
        _dialogs = dialogs;
        _messenger = messenger;
    }

    /// <summary>The tree roots (one: the folder or the solution).</summary>
    public ObservableCollection<ProjectTreeNode> Roots => _workspace.WorkspaceRoots;

    [ObservableProperty] public partial ProjectTreeNode? SelectedNode { get; set; }

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
        _xaml.ClearPreviewTargets();
        SelectedNode = null;
        var root = ProjectTreeNode.CreateDirectory(path);
        root.Populate();
        Roots.Clear();
        Roots.Add(root);
        _languages.SetWorkspace(new LspWorkspace(path, SolutionPath: null));
    }

    private void OpenSolution(string path)
    {
        var root = SolutionLoader.LoadSolution(path, _workspace.Host);
        _xaml.ClearPreviewTargets();
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
        _xaml.ClearPreviewTargets();
        _languages.ClearWorkspace();
    }
}
