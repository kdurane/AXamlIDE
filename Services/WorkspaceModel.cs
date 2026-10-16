using System;
using System.Collections.ObjectModel;
using System.Linq;
// Copy the rest of the using block from MainWindowViewModel.cs if any of these don't resolve
// (ProjectTreeNode, MsBuildHostClient and WorkspaceProjectResolver live in your existing namespaces).
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Services
{
    /// <summary>
    /// The shared "what workspace is open" state that used to be private fields of
    /// MainWindowViewModel: the tree roots, the solution's project list, the MSBuild host
    /// process, and "which project owns this file".
    ///
    /// It is a plain class (not a view model). The XAML language service, and later the
    /// run/debug view model, take it as a dependency instead of reaching into the main VM.
    /// </summary>
    public sealed class WorkspaceModel : IDisposable
    {
        private readonly WorkspaceProjectResolver _projectResolver = new();
        private readonly object _hostGate = new();
        private MsBuildHostClient? _msbuildHost;

        /// <summary>The tree the explorer binds to (MainWindowViewModel.WorkspaceRoots forwards here).</summary>
        public ObservableCollection<ProjectTreeNode> WorkspaceRoots { get; } = [];

        /// <summary>Project files directly under the open solution (empty for a plain folder).</summary>
        public string[] SolutionProjects { get; set; } = [];

        /// <summary>FullPath of the first root node (the solution file, or the folder).</summary>
        public string? RootPath => WorkspaceRoots.FirstOrDefault()?.FullPath;

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
                ? "AXaxmlIDE.msbuildhost.exe"
                : "AXaxmlIDE.msbuildhost";

            var overridePath = Environment.GetEnvironmentVariable("AXAXMLIDE_HOST");
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
}
