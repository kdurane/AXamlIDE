using System;
using AXamlIDE.Models;
using AXamlIDE.Services.NuGet;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Opens the NuGet manager tab (NuGetDockable) for a project: the explorer's "Manage NuGet Packages...".
/// One tab per project; asking again brings it back.
/// </summary>
public sealed partial class NuGetManager(NuGetService nuget, DockLayoutViewModel docking, IMessenger messenger)
{
    /// <summary>A project node (a solution's project, or a .csproj file in an opened folder).</summary>
    [RelayCommand]
    private void ManagePackages(ProjectTreeNode? node)
    {
        if (node is null || !IsProject(node))
            return;
        if (!docking.ShowPage(NuGetDockable.IdFor(node.FullPath)))
            docking.OpenPage(new NuGetDockable(node.FullPath, nuget, messenger));
    }

    public static bool IsProject(ProjectTreeNode node)
        => node.Kind == NodeKind.Project
           || node.Kind == NodeKind.File && node.FullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
}
