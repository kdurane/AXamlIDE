using System;
using System.IO;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.NuGet;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Opens the NuGet manager tab (NuGetDockable) for a project: the explorer's "Manage NuGet Packages..." and the
/// Tools menu's. One tab per project; asking again brings it back.
/// </summary>
public sealed partial class NuGetManager(
    NuGetService nuget,
    DockLayoutViewModel docking,
    WorkspaceModel workspace,
    WorkspaceViewModel explorer,
    RunViewModel run,
    IMessenger messenger)
{
    /// <summary>A project node (a solution's project, or a .csproj file in an opened folder).</summary>
    [RelayCommand]
    private void ManagePackages(ProjectTreeNode? node)
    {
        if (node is not null && IsProject(node))
            Open(node.FullPath);
    }

    /// <summary>Tools > Manage NuGet Packages: the explorer selection's project, else the startup project.</summary>
    [RelayCommand]
    private void ManagePackagesForCurrentProject()
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
            messenger.Send(new StatusMessage("Select a project in the explorer (or choose a startup project) to manage its NuGet packages."));
    }

    private void Open(string projectPath)
    {
        if (!docking.ShowPage(NuGetDockable.IdFor(projectPath)))
            docking.OpenPage(new NuGetDockable(projectPath, nuget, messenger));
    }

    public static bool IsProject(ProjectTreeNode node)
        => node.Kind == NodeKind.Project
           || node.Kind == NodeKind.File && node.FullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
}
