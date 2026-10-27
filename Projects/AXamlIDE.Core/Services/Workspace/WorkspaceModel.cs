using System;
using System.Collections.Generic;
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

    private readonly object _foundGate = new();
    private (string Root, IReadOnlyList<string> Projects)? _found;
    private string[] _solutionProjects = [];

    /// <summary>Project files directly under the open solution (empty for a plain folder).</summary>
    public string[] SolutionProjects
    {
        get => _solutionProjects;
        set
        {
            _solutionProjects = value;
            lock (_foundGate)
                _found = null;   // set on every open and reopen: look at the folder afresh
        }
    }

    /// <summary>
    /// Every *.csproj under the workspace's folder (<see cref="WorkspaceProjectResolver.FindProjectFiles"/>), found on
    /// first use and then kept. With no solution, this is the workspace's project list: the language server, the
    /// startup projects and "which project owns this file" all use it, rather than each walking the folder its own
    /// way. The preview also looks here for app projects the solution doesn't list. A project created outside the IDE
    /// shows up after Refresh (which reopens the workspace).
    /// </summary>
    public IReadOnlyList<string> ProjectFilesUnderRoot
    {
        get
        {
            if (RootDirectory is not { } root)
                return [];
            lock (_foundGate)
            {
                // Keyed by the folder too, so a list found for the previous workspace is never handed out.
                if (_found is not { } found || found.Root != root)
                    _found = found = (root, WorkspaceProjectResolver.FindProjectFiles(root));
                return found.Projects;
            }
        }
    }

    /// <summary>The workspace's projects: the solution's, or for a plain folder the ones found under it.</summary>
    public IReadOnlyList<string> Projects => SolutionProjects.Length > 0 ? SolutionProjects : ProjectFilesUnderRoot;

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

    /// <summary>
    /// Forget the MSBuild host's remembered evaluations (on open, refresh and close), so a reopened workspace sees
    /// changes the host's own checks can't, like a newly created Directory.Build.props.
    /// </summary>
    public void ClearProjectCaches()
    {
        lock (_hostGate)
            _msbuildHost?.ClearCache();   // not started yet: nothing remembered
    }

    public string? FindProjectForDocument(string documentPath)
        => _projectResolver.FindProjectForDocument(documentPath, Projects, RootPath);

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
