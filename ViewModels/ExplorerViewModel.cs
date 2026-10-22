using System;
using System.IO;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Workspace;
using AXamlIDE.Views;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Explorer panel: the workspace tree plus what you can do to a node (open, refresh, build,
/// run, debug; cut/copy/paste, rename and delete are in ExplorerViewModel.Files.cs).
/// The workspace itself (open/close, selection) is <see cref="WorkspaceViewModel"/>.
/// </summary>
public sealed partial class ExplorerViewModel(
    WorkspaceViewModel workspace,
    WorkspaceModel model,
    RunViewModel run,
    GitViewModel git,
    PropertiesViewModel properties,
    NuGetManager nuget,
    PublishManager publish,
    IDocumentHost documents,
    DocumentManager openDocuments,
    IDialogService dialogs,
    IMessenger messenger,
    ImageViewerManager images)
{
    public WorkspaceViewModel Workspace { get; } = workspace;

    /// <summary>The Git commands for a node's file or folder (the explorer's Git submenu).</summary>
    public GitViewModel Git { get; } = git;

    /// <summary>Right-click > Properties: the panel under the explorer, following the selection.</summary>
    public PropertiesViewModel Properties { get; } = properties;

    /// <summary>Right-click a project > Manage NuGet Packages...</summary>
    public NuGetManager NuGet { get; } = nuget;

    /// <summary>Right-click a project > Publish...</summary>
    public PublishManager Publish { get; } = publish;

    /// <summary>Build / Run / Start Debugging for a node (the node is the command parameter).</summary>
    public RunViewModel Run { get; } = run;

    [RelayCommand]
    private void OpenFile(ProjectTreeNode? node)
    {
        if (node is not { Kind: NodeKind.File } || string.IsNullOrEmpty(node.FullPath))
            return;
        try
        {
            if (!images.TryOpen(node.FullPath))
                documents.OpenAndActivate(node.FullPath);

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
            documents.OpenAndActivate(node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
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

    public void Status(string text) => messenger.Send(new StatusMessage(text));
}
