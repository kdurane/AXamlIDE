using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Running;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The startup project and build configuration (the toolbar's two boxes, like Visual Studio's).
/// Run and Debug start the startup project; when there's more than one candidate and none is chosen, they ask.
/// The startup project is offered from the projects that build an executable, is remembered per workspace
/// (WorkspaceSession, in .axamlide/) and shown bold in the explorer (ExplorerView compares paths).
/// </summary>
public partial class RunViewModel
{
    private string? _rememberedStartupProject;   // from .axamlide/, picked once the list has loaded
    private Task _startupProjectsLoading = Task.CompletedTask;
    private int _startupProjectsGeneration;

    public ObservableCollection<StartupProjectItem> StartupProjects { get; } = [];

    /// <summary>The project Run and Debug start; null until chosen (or found to be the only candidate).</summary>
    [ObservableProperty] public partial StartupProjectItem? StartupProject { get; set; }

    public IReadOnlyList<string> Configurations { get; } = ["Debug", "Release"];

    /// <summary>The build configuration for Build, Run and Debug.</summary>
    [ObservableProperty] public partial string Configuration { get; set; } = "Debug";

    public string? StartupProjectPath => StartupProject?.Path;

    /// <summary>The play button's tooltip: what clicking it will do.</summary>
    public string StartOrStopToolTip => IsRunning
        ? "Stop (Shift+F5)"
        : StartupProject is { } project ? $"Start Debugging {project.Name} (F5)" : "Start Debugging (F5)";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StartOrStopToolTip));

    partial void OnStartupProjectChanged(StartupProjectItem? value)
    {
        OnPropertyChanged(nameof(StartupProjectPath));
        OnPropertyChanged(nameof(StartOrStopToolTip));
    }

    /// <summary>
    /// The toolbar's play button (Visual Studio's): Start Debugging the startup project, or Stop while it runs.
    /// </summary>
    [RelayCommand]
    private Task StartOrStop()
    {
        if (IsRunning || IsDebugging)
        {
            StopRunningProject();
            return Task.CompletedTask;
        }

        return StartDebuggingCommand.ExecuteAsync(null);
    }

    /// <summary>The explorer's "Set as Startup Project".</summary>
    [RelayCommand]
    private void SetStartupProject(ProjectTreeNode? node)
    {
        if (node?.FullPath is not { Length: > 0 } path)
            return;

        if (StartupProjects.FirstOrDefault(item => SamePath(item.Path, path)) is { } item)
            StartupProject = item;
        else
            ShowBuildMessage($"{Path.GetFileNameWithoutExtension(path)} can't be a startup project: it doesn't build an executable (OutputType Exe or WinExe).");
    }

    /// <summary>A workspace was opened: remember its startup project and configuration (WorkspaceSession).</summary>
    public void RestoreStartup(string? startupProject, string? configuration)
    {
        _rememberedStartupProject = startupProject;
        StartupProject = StartupProjects.FirstOrDefault(item => SamePath(item.Path, startupProject));
        Configuration = configuration is not null && Configurations.Contains(configuration) ? configuration : "Debug";
    }

    private void OnWorkspaceOpened() => _startupProjectsLoading = LoadStartupProjectsAsync();

    private void OnWorkspaceClosed()
    {
        _startupProjectsGeneration++;
        _rememberedStartupProject = null;
        StartupProject = null;
        StartupProjects.Clear();
    }

    /// <summary>List the workspace's executable projects (evaluating each one, so off the UI thread).</summary>
    private async Task LoadStartupProjectsAsync()
    {
        var generation = ++_startupProjectsGeneration;
        var candidates = _workspace.Projects;

        var runnable = await Task.Run(() => candidates.Distinct().Where(IsRunnable).ToList());
        if (generation != _startupProjectsGeneration)
            return;   // another workspace was opened meanwhile

        var current = StartupProject?.Path ?? _rememberedStartupProject;
        StartupProjects.Clear();
        foreach (var path in runnable)
            StartupProjects.Add(new StartupProjectItem(DisplayName(path, runnable), path));

        // A refresh keeps the choice; otherwise the remembered one, or the only candidate.
        StartupProject = StartupProjects.FirstOrDefault(item => SamePath(item.Path, current))
                         ?? (StartupProjects.Count == 1 ? StartupProjects[0] : null);
    }

    /// <summary>
    /// The project Run/Debug should start: the one passed (explorer menu), else the startup project, else ask
    /// the user to choose one (and keep it). Null if there's none or they cancelled.
    /// </summary>
    private async Task<string?> ChooseStartupProjectAsync(ProjectTreeNode? explicitNode)
    {
        if (explicitNode is not null)
            return ResolveProject(explicitNode);

        await _startupProjectsLoading;
        if (StartupProject is { } chosen)
            return chosen.Path;

        if (StartupProjects.Count == 0)
        {
            ShowBuildMessage(_explorer.IsOpen
                ? "There's no project to start: none in the workspace builds an executable (OutputType Exe or WinExe)."
                : "Open a folder or solution first.");
            return null;
        }

        var name = await _dialogs.PickAsync("Choose a startup project",
            "Run and Debug start the startup project. You can change it in the toolbar or with the explorer's \"Set as Startup Project\".",
            [.. StartupProjects.Select(item => item.Name)]);
        if (StartupProjects.FirstOrDefault(item => item.Name == name) is not { } picked)
            return null;

        StartupProject = picked;
        return picked.Path;
    }

    /// <summary>The project of a tree node (or of the file it's in), reporting failures in Output.</summary>
    private string? ResolveProject(ProjectTreeNode? node)
    {
        string? projectPath;
        try
        {
            projectPath = _workspace.ResolveProjectPath(node);
        }
        catch (Exception ex)
        {
            ShowBuildMessage($"Could not determine the selected project: {ex.Message}");
            return null;
        }

        if (projectPath is null)
            ShowBuildMessage("Select a project or one of its files in the workspace tree first.");
        return projectPath;
    }

    private bool IsRunnable(string projectPath)
    {
        try
        {
            return StartupProjectFinder.IsRunnable(_workspace.Host.GetProjectFacts(projectPath).OutputType);
        }
        catch (InvalidOperationException)
        {
            return false;   // MSBuild couldn't evaluate it
        }
    }

    /// <summary>The project's name, with its folder when another candidate has the same name.</summary>
    private string DisplayName(string path, IReadOnlyList<string> all)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var shared = all.Count(other => Path.GetFileNameWithoutExtension(other) == name) > 1;
        if (!shared || _workspace.RootDirectory is not { } root)
            return name;
        return $"{name} ({Path.GetRelativePath(root, Path.GetDirectoryName(path)!)})";
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null && PathComparer.Equals(a, b);
}
