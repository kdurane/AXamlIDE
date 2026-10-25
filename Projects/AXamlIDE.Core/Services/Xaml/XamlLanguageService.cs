using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.CompletionEngine.Completion;
using AXamlIDE.Models;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// Everything AXAML-aware that the editor and preview need from the project system:
/// completions, hover, go-to-definition (decompile), and the preview target.
/// </summary>
public sealed class XamlLanguageService
{
    private readonly WorkspaceModel _workspace;
    private readonly XamlCompletionService _xamlCompletion = new();
    private readonly XamlDecompilationService _xamlDecompilation = new();
    private XamlPreviewTargetResolver? _previewTargetResolver;

    private readonly ConcurrentDictionary<string, Lazy<Task<XamlPreviewTarget>>> _previewTargets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<ProjectXamlInfo>>> _projects =
        new(StringComparer.OrdinalIgnoreCase);

    public XamlLanguageService(WorkspaceModel workspace) => _workspace = workspace;

    /// <summary>Call when a folder/solution is opened or closed.</summary>
    public void ClearProjectCaches()
    {
        _previewTargets.Clear();
        _projects.Clear();
    }

    public async Task<CompletionSet?> GetCompletionsAsync(string documentPath, string text, int caretOffset)
    {
        var project = await GetProjectAsync(documentPath);
        return await _xamlCompletion.GetCompletionsAsync(
            project.ProjectPath, project.AssemblyName, text, caretOffset, project.OutputPath, project.AssetsPath);
    }

    public async Task<string?> GetHoverAsync(string documentPath, string text, int offset)
    {
        var project = await GetProjectAsync(documentPath);
        return await _xamlCompletion.GetHoverAsync(
            project.ProjectPath, project.AssemblyName, text, offset, project.OutputPath, project.AssetsPath);
    }

    private async Task<XamlCompletionService.NavigationTarget?> GetNavigationTargetAsync(
        string documentPath, string text, int offset)
    {
        var project = await GetProjectAsync(documentPath);
        return await _xamlCompletion.GetNavigationTargetAsync(
            project.ProjectPath, project.AssemblyName, text, offset, project.OutputPath, project.AssetsPath);
    }

    /// <summary>The view-model property the binding path at the offset names (hover and F12 in {Binding ...}).</summary>
    public async Task<XamlCompletionService.BindingTarget?> GetBindingTargetAsync(string documentPath, string text, int offset)
    {
        var project = await GetProjectAsync(documentPath);
        return await _xamlCompletion.GetBindingTargetAsync(
            project.ProjectPath, project.AssemblyName, text, offset, project.OutputPath, project.AssetsPath);
    }

    /// <summary>Decompiled source of a compiled type (for a binding whose view model isn't in the workspace).</summary>
    public async Task<DecompiledSource?> DecompileAsync(XamlCompletionService.NavigationTarget target)
        => await _xamlDecompilation.DecompileTypeAsync(target.AssemblyPath, target.TypeFullName, target.DocumentationPath);

    public async Task<DecompiledSource?> DecompileTypeAsync(string documentPath, string text, int offset)
    {
        var target = await GetNavigationTargetAsync(documentPath, text, offset);
        return target is null
            ? null
            : await _xamlDecompilation.DecompileTypeAsync(target.AssemblyPath, target.TypeFullName, target.DocumentationPath);
    }

    public Task<XamlPreviewTarget> GetPreviewTargetAsync(string documentPath)
    {
        var projectPath = _workspace.FindProjectForDocument(documentPath)
            ?? throw new InvalidOperationException("Could not find a .csproj containing this AXAML file.");
        var key = Path.GetFullPath(projectPath);
        var projects = _workspace.SolutionProjects.ToArray();
        var workspaceRoot = _workspace.RootPath;
        return GetCachedPreviewTargetAsync(key, projects, workspaceRoot);
    }

    private async Task<XamlPreviewTarget> GetCachedPreviewTargetAsync(
        string projectPath,
        string[] solutionProjects,
        string? workspaceRoot)
    {
        while (true)
        {
            var entry = _previewTargets.GetOrAdd(projectPath, path =>
                new Lazy<Task<XamlPreviewTarget>>(
                    () => Task.Run(() => ResolvePreviewTarget(path, solutionProjects, workspaceRoot))));
            XamlPreviewTarget target;
            try
            {
                target = await entry.Value;
            }
            catch
            {
                RemovePreviewTarget(projectPath, entry);
                throw;
            }

            if (target.IsCurrent)
                return target;
            RemovePreviewTarget(projectPath, entry);
        }
    }

    private void RemovePreviewTarget(string projectPath, Lazy<Task<XamlPreviewTarget>> entry)
    {
        ((ICollection<KeyValuePair<string, Lazy<Task<XamlPreviewTarget>>>>)_previewTargets)
            .Remove(new KeyValuePair<string, Lazy<Task<XamlPreviewTarget>>>(projectPath, entry));
    }

    /// <summary>
    /// What completion and hover need from the project system: its output path, assets file and assembly name.
    /// Asked of the MSBuild host once per project, off the UI thread (completion runs on every keystroke, and
    /// each question is an MSBuild evaluation). Asked again when the .csproj changes, when the project had no
    /// assets file yet (not restored), and after a failure.
    /// </summary>
    private async Task<ProjectXamlInfo> GetProjectAsync(string documentPath)
    {
        var projectPath = _workspace.FindProjectForDocument(documentPath)
            ?? throw new InvalidOperationException("Could not find a .csproj containing this AXAML file.");
        var key = Path.GetFullPath(projectPath);
        while (true)
        {
            var entry = _projects.GetOrAdd(key, _ => new Lazy<Task<ProjectXamlInfo>>(
                () => Task.Run(() => QueryProject(projectPath))));
            ProjectXamlInfo project;
            try
            {
                project = await entry.Value;
            }
            catch
            {
                RemoveProject(key, entry);
                throw;
            }

            if (File.GetLastWriteTimeUtc(project.ProjectPath) != project.ProjectWriteTime)
            {
                RemoveProject(key, entry);   // the .csproj changed: ask again now
                continue;
            }

            if (project.AssetsPath is null)
                RemoveProject(key, entry);   // not restored yet: ask again next time
            return project;
        }
    }

    private ProjectXamlInfo QueryProject(string projectPath)
        => new(projectPath,
            _workspace.Host.GetProjectOutputPath(projectPath),
            _workspace.Host.GetProjectAssetsFile(projectPath),
            _workspace.Host.GetProjectAssemblyName(projectPath),
            File.GetLastWriteTimeUtc(projectPath));

    private void RemoveProject(string key, Lazy<Task<ProjectXamlInfo>> entry)
        => ((ICollection<KeyValuePair<string, Lazy<Task<ProjectXamlInfo>>>>)_projects)
            .Remove(new KeyValuePair<string, Lazy<Task<ProjectXamlInfo>>>(key, entry));

    private sealed record ProjectXamlInfo(
        string ProjectPath, string? OutputPath, string? AssetsPath, string AssemblyName, DateTime ProjectWriteTime);

    private XamlPreviewTarget ResolvePreviewTarget(string xamlProjectPath, string[] solutionProjects, string? workspaceRoot)
        => (_previewTargetResolver ??= new XamlPreviewTargetResolver(_workspace.Host))
            .Resolve(xamlProjectPath, solutionProjects, workspaceRoot);
}
