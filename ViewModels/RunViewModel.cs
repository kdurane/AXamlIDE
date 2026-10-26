using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Running;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>What the Build menu's three commands do (Visual Studio's Build, Rebuild and Clean).</summary>
public enum BuildKind { Build, Rebuild, Clean }

/// <summary>
/// Build, run and (in RunViewModel.Debug.cs) debug a project, plus the Output and Console
/// text they produce. Run and Debug start the startup project (RunViewModel.Startup.cs) unless a
/// project node is passed as the command parameter (the explorer's menu); Build builds the project
/// selected in the explorer, else the startup project. All use the toolbar's configuration.
///
/// Talks out through messages: StatusMessage, ShowToolTabMessage, and (while debugging)
/// NavigateToDebugLocationMessage / DebugSnapshotChangedMessage for the editor.
/// </summary>
public partial class RunViewModel : ObservableObject, IDisposable
{
    private readonly DotNetProjectRunner _projectRunner = new();
    private readonly WorkspaceModel _workspace;
    private readonly WorkspaceViewModel _explorer;
    private readonly DocumentManager _documents;
    private readonly DocumentSaveService _saver;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private bool _runStopRequested;

    public RunViewModel(
        WorkspaceModel workspace,
        WorkspaceViewModel explorer,
        DocumentManager documents,
        DocumentSaveService saver,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _workspace = workspace;
        _explorer = explorer;
        _documents = documents;
        _saver = saver;
        _dialogs = dialogs;
        _messenger = messenger;

        _projectRunner.OutputReceived += AppendProcessOutput;

        // Breakpoints of open documents are synced to the debugger.
        _documents.Opened += TrackDocument;
        _documents.Closing += UntrackDocument;

        explorer.Opened += OnWorkspaceOpened;
        explorer.Closed += OnWorkspaceClosed;
    }

    public ObservableCollection<string> BuildOutput { get; } = [];
    public ObservableCollection<string> ConsoleOutput { get; } = [];

    /// <summary>A Run or Debug session is going (the toolbar's play button is a stop button then).</summary>
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] private partial bool IsBusy { get; set; }

    // ---------------------------------------------------------------- build

    /// <summary>
    /// Build (the Build menu, Ctrl+Shift+B and the top bar's button), like Visual Studio's Build Solution: the open
    /// solution, else the startup project, else the explorer selection's project (asking when that's ambiguous too).
    /// Rebuild and Clean act on the same thing.
    /// </summary>
    [RelayCommand]
    private Task Build() => BuildWorkspaceAsync(BuildKind.Build);

    /// <summary>Rebuild Solution (Ctrl+Alt+F7, VS's key): everything again, ignoring what's up to date.</summary>
    [RelayCommand]
    private Task Rebuild() => BuildWorkspaceAsync(BuildKind.Rebuild);

    /// <summary>Clean Solution: delete the build output of the toolbar's configuration.</summary>
    [RelayCommand]
    private Task Clean() => BuildWorkspaceAsync(BuildKind.Clean);

    /// <summary>The explorer's "Build" on a project (or a file in it); null = the explorer's selection.</summary>
    [RelayCommand]
    private Task BuildProject(ProjectTreeNode? selection) => BuildProjectAsync(selection, BuildKind.Build);

    [RelayCommand]
    private Task RebuildProject(ProjectTreeNode? selection) => BuildProjectAsync(selection, BuildKind.Rebuild);

    [RelayCommand]
    private Task CleanProject(ProjectTreeNode? selection) => BuildProjectAsync(selection, BuildKind.Clean);

    /// <summary>The solution, else the startup project, else the explorer selection's project (asking if needed).</summary>
    private async Task BuildWorkspaceAsync(BuildKind kind)
    {
        var path = _explorer.SolutionPath
                   ?? StartupProjectPath
                   ?? (_explorer.SelectedNode is { } node ? ResolveProject(node) : await ChooseStartupProjectAsync(null));
        if (path is not null)
            await BuildAsync(path, kind);
    }

    private async Task BuildProjectAsync(ProjectTreeNode? selection, BuildKind kind)
    {
        if (ResolveProject(selection ?? _explorer.SelectedNode) is { } projectPath)
            await BuildAsync(projectPath, kind);
    }

    private async Task BuildAsync(string path, BuildKind kind = BuildKind.Build)
    {
        // Like other IDEs: save everything before building (a clean doesn't read the sources).
        if (IsBusy || kind != BuildKind.Clean && !await _saver.SaveAllAsync())
            return;

        var configuration = Configuration;
        var (doing, done) = kind switch
        {
            BuildKind.Rebuild => ("Rebuilding", "Rebuild"),
            BuildKind.Clean => ("Cleaning", "Clean"),
            _ => ("Building", "Build")
        };

        _messenger.Send(new BuildStartedMessage());
        BeginBuildOutput($"{doing} {Path.GetFileName(path)} ({configuration})");
        IsBusy = true;
        try
        {
            var exitCode = await BuildWithNoticeAsync(() => kind switch
            {
                BuildKind.Rebuild => _projectRunner.RebuildAsync(path, configuration),
                BuildKind.Clean => _projectRunner.CleanAsync(path, configuration),
                _ => _projectRunner.BuildAsync(path, configuration)
            });
            AppendBuildOutput(exitCode == 0
                ? $"{done} succeeded."
                : $"{done} failed with exit code {exitCode}.");
        }
        catch (Exception ex)
        {
            AppendBuildOutput($"{done} failed: {ex}");
        }
        finally
        {
            IsBusy = false;
            _messenger.Send(new BuildFinishedMessage(true));
        }
    }

    // ------------------------------------------------------------------ run

    [RelayCommand]
    private async Task RunProjectAsync(ProjectTreeNode? selection)
    {
        if (IsBusy || IsRunning || await ChooseStartupProjectAsync(selection) is not { } projectPath)
            return;

        if (!await _saver.SaveAllAsync())   // like other IDEs: save everything before building
            return;

        var configuration = Configuration;
        BeginBuildOutput($"Building {Path.GetFileName(projectPath)} ({configuration}) before running");
        IsBusy = true;

        try
        {
            var buildExitCode = await _projectRunner.BuildAsync(projectPath, configuration);
            if (buildExitCode != 0)
            {
                AppendBuildOutput($"Run cancelled because the build failed with exit code {buildExitCode}.");
                return;
            }

            BeginRunOutput($"Running {Path.GetFileName(projectPath)} ({configuration})");
            IsRunning = true;
            var runExitCode = await _projectRunner.RunAsync(projectPath, configuration);
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


    /// <summary>Runs a build, telling the rest of the app (the previews) when it starts and finishes.</summary>
    private async Task<int> BuildWithNoticeAsync(Func<Task<int>> build)
    {
        _messenger.Send(new BuildStartedMessage());
        var exitCode = -1;
        try
        {
            exitCode = await build();
            return exitCode;
        }
        finally
        {
            _messenger.Send(new BuildFinishedMessage(exitCode == 0));
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

    // --------------------------------------------------------------- output

    private void BeginBuildOutput(string message)
    {
        BuildOutput.Clear();
        _messenger.Send(new ShowToolTabMessage(ToolTab.Output));
        AppendBuildOutput(message);
    }

    private void BeginRunOutput(string message)
    {
        ConsoleOutput.Clear();
        _messenger.Send(new ShowToolTabMessage(ToolTab.Console));
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
