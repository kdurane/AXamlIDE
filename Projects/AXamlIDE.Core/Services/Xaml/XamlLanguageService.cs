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

    public XamlLanguageService(WorkspaceModel workspace) => _workspace = workspace;

    /// <summary>Call when a folder/solution is opened or closed.</summary>
    public void ClearProjectCaches()
    {
        _previewTargets.Clear();
        _xamlCompletion.ForgetBuiltAssemblies();
    }

    /// <summary>A build finished: completion looks for the project's DLL again, as the build may have moved it.</summary>
    public void OnBuildFinished() => _xamlCompletion.ForgetBuiltAssemblies();

    /// <summary>
    /// A file in the workspace was saved, added or removed (on disk). An .axaml one can add or remove x:Keys and style
    /// classes that completion offers, so its project's scan is redone on the next completion.
    /// </summary>
    public void OnFileChanged(string path)
    {
        if (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            _xamlCompletion.ForgetProjectXaml(path);
    }

    public async Task<CompletionSet?> GetCompletionsAsync(string documentPath, string text, int caretOffset)
    {
        var project = await GetProjectAsync(documentPath);
        return await _xamlCompletion.GetCompletionsAsync(
            project.ProjectPath, project.AssemblyName, text, caretOffset, project.OutputPath, project.AssetsPath);
    }

    public async Task<XamlCompletionService.XamlHover?> GetHoverAsync(string documentPath, string text, int offset)
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
        // The solution's projects first (the app is usually there), then any others under the folder. Read here, on
        // the caller's thread: the workspace's lists aren't for reading from the background.
        string[] projects = [.. _workspace.SolutionProjects, .. _workspace.ProjectFilesUnderRoot];
        return GetCachedPreviewTargetAsync(key, projects);
    }

    private async Task<XamlPreviewTarget> GetCachedPreviewTargetAsync(string projectPath, string[] workspaceProjects)
    {
        while (true)
        {
            var entry = _previewTargets.GetOrAdd(projectPath, path =>
                new Lazy<Task<XamlPreviewTarget>>(
                    () => Task.Run(() => ResolvePreviewTarget(path, workspaceProjects))));
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
    /// Asked of the MSBuild host off the UI thread (completion runs on every keystroke, and an evaluation takes a
    /// while); the host remembers its evaluations until the project or its imports change, so most calls are quick.
    /// </summary>
    private async Task<ProjectXamlInfo> GetProjectAsync(string documentPath)
    {
        var projectPath = _workspace.FindProjectForDocument(documentPath)
            ?? throw new InvalidOperationException("Could not find a .csproj containing this AXAML file.");
        var facts = await Task.Run(() => _workspace.Host.GetProjectFacts(projectPath));
        return new ProjectXamlInfo(projectPath, facts.OutputPath, facts.AssetsFile, facts.AssemblyName);
    }

    private sealed record ProjectXamlInfo(string ProjectPath, string? OutputPath, string? AssetsPath, string AssemblyName);

    private XamlPreviewTarget ResolvePreviewTarget(string xamlProjectPath, string[] workspaceProjects)
        => (_previewTargetResolver ??= new XamlPreviewTargetResolver(_workspace.Host))
            .Resolve(xamlProjectPath, workspaceProjects);
}
