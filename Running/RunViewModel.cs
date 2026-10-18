using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Documents;
using AXamlIDE.Shell;
using AXamlIDE.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.Running;

/// <summary>
/// Build, run and (in RunViewModel.Debug.cs) debug a project, plus the Output and Console
/// text they produce.
///
/// The shell hooks <see cref="ToolTabRequested"/> to its SelectedToolTabIndex, and the
/// document lifecycle calls TrackDocument / UntrackDocument (breakpoint syncing).
/// </summary>
public partial class RunViewModel : ObservableObject, IDisposable
{
    private readonly DotNetProjectRunner _projectRunner = new();
    private readonly WorkspaceModel _workspace;
    private readonly ObservableCollection<OpenDocument> _documents;
    private readonly Func<ProjectTreeNode?> _getSelectedNode;
    private readonly Action<string> _showStatus;
    private bool _runStopRequested;

    public RunViewModel(
        WorkspaceModel workspace,
        ObservableCollection<OpenDocument> documents,
        Func<ProjectTreeNode?> getSelectedNode,
        Action<string> showStatus)
    {
        _workspace = workspace;
        _documents = documents;
        _getSelectedNode = getSelectedNode;
        _showStatus = showStatus;

        _projectRunner.OutputReceived += AppendProcessOutput;
    }

    public ObservableCollection<string> BuildOutput { get; } = [];
    public ObservableCollection<string> ConsoleOutput { get; } = [];

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] private partial bool IsBusy { get; set; }

    /// <summary>
    /// Explicit startup project; wins over the tree selection when set.
    /// Nothing sets it yet (intended for a "Set as Startup Project" menu item).
    /// </summary>
    public string? StartupProjectPath { get; set; }

    /// <summary>Ask the shell to switch the bottom panel to a tab (see <see cref="ToolTab"/>).</summary>
    public event Action<int>? ToolTabRequested;

    // ---------------------------------------------------------------- build

    [RelayCommand]
    private async Task BuildProject(ProjectTreeNode? selection)
    {
        var selected = selection ?? _getSelectedNode();
        string? projectPath;
        try
        {
            projectPath = _workspace.ResolveProjectPath(selected);
        }
        catch (Exception ex)
        {
            ShowBuildMessage($"Could not determine the selected project: {ex}");
            return;
        }

        if (projectPath is null)
        {
            ShowBuildMessage("Select a project or one of its files in the workspace tree first.");
            return;
        }

        if (IsBusy)
            return;

        BeginBuildOutput($"Building {Path.GetFileName(projectPath)} (Debug)");
        IsBusy = true;
        try
        {
            var exitCode = await _projectRunner.BuildAsync(projectPath);
            AppendBuildOutput(exitCode == 0
                ? "Build succeeded."
                : $"Build failed with exit code {exitCode}.");
        }
        catch (Exception ex)
        {
            AppendBuildOutput($"Build failed: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ run

    [RelayCommand]
    private async Task RunProjectAsync(ProjectTreeNode? selection)
    {
        var selected = selection ?? _getSelectedNode();
        string? projectPath;
        try
        {
            projectPath = _workspace.ResolveProjectPath(selected);
        }
        catch (Exception ex)
        {
            ShowBuildMessage($"Could not determine the selected project: {ex}");
            return;
        }

        if (projectPath is null)
        {
            ShowBuildMessage("Select a project or one of its files in the workspace tree first.");
            return;
        }

        if (IsBusy)
            return;

        BeginBuildOutput($"Building {Path.GetFileName(projectPath)} (Debug) before running");
        IsBusy = true;
        try
        {
            var buildExitCode = await _projectRunner.BuildAsync(projectPath);
            if (buildExitCode != 0)
            {
                AppendBuildOutput($"Run cancelled because the build failed with exit code {buildExitCode}.");
                return;
            }

            BeginRunOutput($"Running {Path.GetFileName(projectPath)} (Debug)");
            IsRunning = true;
            var runExitCode = await _projectRunner.RunAsync(projectPath);
            AppendConsoleOutput(_runStopRequested
                ? "Application stopped."
                : runExitCode == 0
                    ? "Application exited."
                    : $"Application exited with code {runExitCode}.");
        }
        catch (Exception ex)
        {
            AppendConsoleOutput($"Run failed: {ex}");
        }
        finally
        {
            IsRunning = false;
            _runStopRequested = false;
            IsBusy = false;
        }
    }

    /// <summary>Stops the debug session if there is one, otherwise the running process.</summary>
    [RelayCommand]
    public void StopRunningProject()
    {
        if (_debugSession is { } session)
        {
            _ = StopDebugSessionAsync(session);
            return;
        }

        if (!IsRunning)
            return;

        _runStopRequested = true;
        AppendConsoleOutput("Stopping application...");
        _projectRunner.Stop();
    }

    // ------------------------------------------------------ startup project

    private string? ResolveStartupProject(ProjectTreeNode? selection)
    {
        // 1. Explicit startup project wins.
        if (StartupProjectPath is not null && File.Exists(StartupProjectPath))
            return StartupProjectPath;

        // 2. The selected project, if it can actually run.
        var selected = _workspace.ResolveProjectPath(selection);   // fine with a null selection
        if (selected is not null && IsRunnable(selected))
            return selected;

        // 3. Otherwise, the only executable project in the workspace.
        var exes = _workspace.SolutionProjects.Where(IsRunnable).Distinct().ToList();
        return exes.Count == 1 ? exes[0] : null;
    }

    private bool IsRunnable(string projectPath)
        => _workspace.Host.GetProjectOutputType(projectPath) is { } t
           && (t.Equals("Exe", StringComparison.OrdinalIgnoreCase)
               || t.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    // --------------------------------------------------------------- output

    private void BeginBuildOutput(string message)
    {
        BuildOutput.Clear();
        ToolTabRequested?.Invoke(ToolTab.Output);
        AppendBuildOutput(message);
    }

    private void BeginRunOutput(string message)
    {
        ConsoleOutput.Clear();
        ToolTabRequested?.Invoke(ToolTab.Console);
        AppendConsoleOutput(message);
    }

    private void ShowBuildMessage(string message) => BeginBuildOutput(message);

    private void AppendBuildOutput(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            BuildOutput.Add(message);
        else
            Dispatcher.UIThread.Post(() => BuildOutput.Add(message));
    }

    private void AppendConsoleOutput(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ConsoleOutput.Add(message);
        else
            Dispatcher.UIThread.Post(() => ConsoleOutput.Add(message));
    }

    private void AppendProcessOutput(string message, bool isRun)
    {
        if (isRun)
            AppendConsoleOutput(message);
        else
            AppendBuildOutput(message);
    }

    public void Dispose()
    {
        _debugSession?.Dispose();
        _debugSession = null;
        _projectRunner.Dispose();
    }
}
