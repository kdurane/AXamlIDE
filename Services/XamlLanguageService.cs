using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
// Copy the rest of the using block from MainWindowViewModel.cs if any of these don't resolve
// (CompletionSet, DecompiledSource, XamlPreviewTarget, XamlCompletionService, ...).
using AXaxmlIDE.CompletionEngine;
using AXaxmlIDE.CompletionEngine.Completion;
using AXaxmlIDE.Models;
using AXaxmlIDE.ViewModels;

namespace AXaxmlIDE.Services;

/// <summary>
/// Everything AXAML-aware that the editor and preview need from the project system:
/// completions, hover, go-to-definition (decompile), and the preview target.
/// Bodies are the old MainWindowViewModel.GetXaml* methods; the changes are
///   Host                       -> _workspace.Host
///   FindProjectForDocument     -> _workspace.FindProjectForDocument
///   _solutionProjects          -> _workspace.SolutionProjects
///   WorkspaceRoots...FullPath  -> _workspace.RootPath
/// </summary>
public sealed class XamlLanguageService
{
    private readonly WorkspaceModel _workspace;
    private readonly XamlCompletionService _xamlCompletion = new();
    private readonly XamlDecompilationService _xamlDecompilation = new();
    private XamlPreviewTargetResolver? _previewTargetResolver;

    private readonly ConcurrentDictionary<string, Lazy<Task<XamlPreviewTarget>>> _previewTargets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _projectAssemblyNames =
        new(StringComparer.OrdinalIgnoreCase);

    public XamlLanguageService(WorkspaceModel workspace) => _workspace = workspace;

    /// <summary>Call when a folder/solution is opened or closed (was _previewTargets.Clear()).</summary>
    public void ClearPreviewTargets() => _previewTargets.Clear();

    public async Task<CompletionSet?> GetCompletionsAsync(string documentPath, string text, int caretOffset)
    {
        var projectPath = _workspace.FindProjectForDocument(documentPath);
        if (projectPath is null)
            throw new InvalidOperationException("Could not find a .csproj containing this AXAML file.");

        var projectKey = Path.GetFullPath(projectPath);
        var outputPath = _workspace.Host.GetProjectOutputPath(projectPath);
        var assetsPath = _workspace.Host.GetProjectAssetsFile(projectPath);
        var assemblyNameTask = _projectAssemblyNames.GetOrAdd(
            projectKey,
            path => new Lazy<Task<string>>(() => Task.Run(() => _workspace.Host.GetProjectAssemblyName(path)))).Value;
        var assemblyName = await assemblyNameTask;
        return await _xamlCompletion.GetCompletionsAsync(
            projectPath, assemblyName, text, caretOffset, outputPath, assetsPath);
    }

    public async Task<string?> GetHoverAsync(string documentPath, string text, int offset)
    {
        var projectPath = _workspace.FindProjectForDocument(documentPath)
            ?? throw new InvalidOperationException("Could not find a .csproj containing this AXAML file.");
        var projectKey = Path.GetFullPath(projectPath);
        var outputPath = _workspace.Host.GetProjectOutputPath(projectPath);
        var assetsPath = _workspace.Host.GetProjectAssetsFile(projectPath);
        var assemblyName = await _projectAssemblyNames.GetOrAdd(
            projectKey,
            path => new Lazy<Task<string>>(() => Task.Run(() => _workspace.Host.GetProjectAssemblyName(path)))).Value;
        return await _xamlCompletion.GetHoverAsync(
            projectPath, assemblyName, text, offset, outputPath, assetsPath);
    }

    private async Task<XamlCompletionService.NavigationTarget?> GetNavigationTargetAsync(
        string documentPath, string text, int offset)
    {
        var projectPath = _workspace.FindProjectForDocument(documentPath)
            ?? throw new InvalidOperationException("Could not find a .csproj containing this AXAML file.");
        var projectKey = Path.GetFullPath(projectPath);
        var outputPath = _workspace.Host.GetProjectOutputPath(projectPath);
        var assetsPath = _workspace.Host.GetProjectAssetsFile(projectPath);
        var assemblyName = await _projectAssemblyNames.GetOrAdd(
            projectKey,
            path => new Lazy<Task<string>>(() => Task.Run(() => _workspace.Host.GetProjectAssemblyName(path)))).Value;
        return await _xamlCompletion.GetNavigationTargetAsync(
            projectPath, assemblyName, text, offset, outputPath, assetsPath);
    }

    public async Task<DecompiledSource?> DecompileTypeAsync(string documentPath, string text, int offset)
    {
        var target = await GetNavigationTargetAsync(documentPath, text, offset);
        return target is null
            ? null
            : await _xamlDecompilation.DecompileTypeAsync(target.AssemblyPath, target.TypeFullName);
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

    private XamlPreviewTarget ResolvePreviewTarget(string xamlProjectPath, string[] solutionProjects, string? workspaceRoot)
        => (_previewTargetResolver ??= new XamlPreviewTargetResolver(_workspace.Host))
            .Resolve(xamlProjectPath, solutionProjects, workspaceRoot);
}
