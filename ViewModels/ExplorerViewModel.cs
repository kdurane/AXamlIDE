using System;
using System.IO;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Explorer panel: the workspace tree plus what you can do to a node (open, refresh, build,
/// run, debug; cut/copy/paste, rename and delete are in ExplorerViewModel.Files.cs).
/// The workspace itself (open/close, selection) is <see cref="WorkspaceViewModel"/>.
/// Like Visual Studio's Solution Explorer, it can follow the document being edited (Track Active Item, a setting).
/// </summary>
public sealed partial class ExplorerViewModel
{
    private readonly WorkspaceModel _model;
    private readonly IDocumentHost _documents;
    private readonly DocumentManager _openDocuments;
    private readonly TerminalViewModel _terminal;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private readonly ImageViewerManager _images;

    public ExplorerViewModel(
        WorkspaceViewModel workspace,
        WorkspaceModel model,
        RunViewModel run,
        GitViewModel git,
        PropertiesViewModel properties,
        NuGetManager nuget,
        PublishManager publish,
        TerminalViewModel terminal,
        IDocumentHost documents,
        DocumentManager openDocuments,
        IDialogService dialogs,
        IMessenger messenger,
        ImageViewerManager images,
        IEditorSettings settings)
    {
        Workspace = workspace;
        _model = model;
        Run = run;
        Git = git;
        Properties = properties;
        NuGet = nuget;
        Publish = publish;
        _terminal = terminal;
        _documents = documents;
        _openDocuments = openDocuments;
        _dialogs = dialogs;
        _messenger = messenger;
        _images = images;

        openDocuments.ActiveChanged += document =>
        {
            if (settings.Features.TrackActiveItem)
                _ = TrackActiveAsync(document);
        };
    }

    public WorkspaceViewModel Workspace { get; }

    /// <summary>The Git commands for a node's file or folder (the explorer's Git submenu).</summary>
    public GitViewModel Git { get; }

    /// <summary>Right-click > Properties: the panel under the explorer, following the selection.</summary>
    public PropertiesViewModel Properties { get; }

    /// <summary>Right-click a project > Manage NuGet Packages...</summary>
    public NuGetManager NuGet { get; }

    /// <summary>Right-click a project > Publish...</summary>
    public PublishManager Publish { get; }

    /// <summary>Build / Run / Start Debugging for a node (the node is the command parameter).</summary>
    public RunViewModel Run { get; }

    [RelayCommand]
    private void OpenFile(ProjectTreeNode? node)
    {
        if (node is not { Kind: NodeKind.File } || string.IsNullOrEmpty(node.FullPath))
            return;
        try
        {
            if (!_images.TryOpen(node.FullPath))
                _documents.OpenAndActivate(node.FullPath);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
    }

    /// <summary>Right-click a project > Open Project File: the .csproj behind the project node.</summary>
    [RelayCommand]
    private void OpenProjectFile(ProjectTreeNode? node)
    {
        if (node is not { Kind: NodeKind.Project } || string.IsNullOrEmpty(node.FullPath))
            return;
        try
        {
            _documents.OpenAndActivate(node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
    }

    /// <summary>Visual Studio's Open Containing Folder: the system file manager on the node's folder.</summary>
    [RelayCommand]
    private async Task OpenContainingFolder(ProjectTreeNode? node)
    {
        if (node is null || string.IsNullOrEmpty(node.FullPath))
            return;
        try
        {
            await _dialogs.OpenContainingFolderAsync(node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status($"Couldn't open the folder: {ex.Message}");
        }
    }

    /// <summary>Visual Studio's Open in Terminal: the Terminal panel, with a shell in the node's folder.</summary>
    [RelayCommand]
    private void OpenInTerminal(ProjectTreeNode? node)
    {
        if (node is null || FolderFor(node) is not { } folder || !Directory.Exists(folder))
            return;
        _terminal.StartIn(folder);
        _messenger.Send(new ShowToolTabMessage(ToolTab.Terminal));
        _terminal.RequestFocus();
    }

    [RelayCommand]
    private async Task RefreshAsync(ProjectTreeNode? node)
    {
        if (node is null)
            return;
        try
        {
            await Workspace.RefreshAsync(node);
        }
        catch (Exception ex)
        {
            Status($"Couldn't refresh '{node.Name}': {ex.Message}");
        }
    }

    /// <summary>The header's Collapse All.</summary>
    [RelayCommand]
    private void CollapseAll() => Workspace.CollapseAll();

    /// <summary>The header's Sync with Active Document: select the open document's file in the tree.</summary>
    [RelayCommand]
    private Task SyncWithActiveDocument() => TrackActiveAsync(_openDocuments.Active);

    /// <summary>Track Active Item: the selected tab's file is selected in the tree (folders opened as needed).</summary>
    private async Task TrackActiveAsync(OpenDocument? document)
    {
        if (document is null || document.IsReadOnly || !Workspace.IsOpen)
            return;
        try
        {
            if (await Workspace.FindOrLoadAsync(document.Path) is { } node && ReferenceEquals(_openDocuments.Active, document))
                Workspace.SelectedNode = node;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a folder that can't be read: the tree just doesn't follow this one
        }
    }

    public void Status(string text) => _messenger.Send(new StatusMessage(text));
}
