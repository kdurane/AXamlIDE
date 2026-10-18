using System;
using System.Collections.ObjectModel;
using System.Linq;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// The shared "what workspace is open" state: the tree roots, the solution's project list,
/// the MSBuild host process, and "which project owns this file".
///
/// A plain class, not a view model. Anything that needs to know about the open workspace
/// (XAML language service, run/debug) takes this as a dependency.
/// </summary>
public sealed class WorkspaceModel : IDisposable
{
    private readonly WorkspaceProjectResolver _projectResolver = new();
    private readonly object _hostGate = new();
    private MsBuildHostClient? _msbuildHost;

    /// <summary>The tree the solution explorer binds to.</summary>
    public ObservableCollection<ProjectTreeNode> WorkspaceRoots { get; } = [];

    /// <summary>Project files directly under the open solution (empty for a plain folder).</summary>
    public string[] SolutionProjects { get; set; } = [];

    /// <summary>FullPath of the first root node (the solution file, or the folder).</summary>
    public string? RootPath => WorkspaceRoots.FirstOrDefault()?.FullPath;

    /// <summary>The directory files live under: the open folder, or the solution file's folder.</summary>
    public string? RootDirectory => WorkspaceRoots.FirstOrDefault() is not { } root
        ? null
        : root.Kind == NodeKind.Solution ? System.IO.Path.GetDirectoryName(root.FullPath) : root.FullPath;

    public MsBuildHostClient Host
    {
        get
        {
            lock (_hostGate)
                return _msbuildHost ??= new MsBuildHostClient(ResolveHostExecutablePath());
        }
    }

    public string? FindProjectForDocument(string documentPath)
        => _projectResolver.FindProjectForDocument(documentPath, SolutionProjects, RootPath);

    public string? ResolveProjectPath(ProjectTreeNode? selection)
        => _projectResolver.ResolveSelectedProject(selection, WorkspaceRoots);

    private static string ResolveHostExecutablePath()
    {
        var executable = OperatingSystem.IsWindows()
            ? "AXamlIDE.msbuildhost.exe"
            : "AXamlIDE.msbuildhost";

        var overridePath = Environment.GetEnvironmentVariable("AXAMLIDE_HOST");
        if (!string.IsNullOrEmpty(overridePath) && System.IO.File.Exists(overridePath))
            return overridePath;

        return System.IO.Path.Combine(AppContext.BaseDirectory, "msbuildhost", executable);
    }

    public void Dispose()
    {
        lock (_hostGate)
        {
            _msbuildHost?.Dispose();
            _msbuildHost = null;
        }
    }
}
