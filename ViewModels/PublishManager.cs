using System;
using System.IO;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Opens the Publish page (PublishDockable) for a project: the explorer's "Publish..." on a project, and Build >
/// Publish Selected Project (Shift+Alt+P) for the explorer selection's project, else the startup project.
/// One tab per project; asking again brings it back.
/// </summary>
public sealed partial class PublishManager(
    DockLayoutViewModel docking,
    WorkspaceModel workspace,
    WorkspaceViewModel explorer,
    RunViewModel run,
    IDialogService dialogs,
    IDocumentHost documents,
    IMessenger messenger)
{
    /// <summary>A project node (a solution's project, or a .csproj file in an opened folder).</summary>
    [RelayCommand]
    private void PublishProject(ProjectTreeNode? node)
    {
        if (node is not null && NuGetManager.IsProject(node))
            Open(node.FullPath);
    }

    /// <summary>Build > Publish: the explorer selection's project, else the startup project.</summary>
    [RelayCommand]
    private void PublishCurrentProject()
    {
        string? project;
        try
        {
            project = explorer.SelectedNode is { } node ? workspace.ResolveProjectPath(node) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            project = null;
        }

        if ((project ?? run.StartupProjectPath) is { } path)
            Open(path);
        else
            messenger.Send(new StatusMessage("Select a project in the explorer (or choose a startup project) to publish it."));
    }

    private void Open(string projectPath)
    {
        if (!docking.ShowPage(PublishDockable.IdFor(projectPath)))
            docking.OpenPage(new PublishDockable(projectPath, run, workspace, dialogs, documents, messenger));
    }
}
