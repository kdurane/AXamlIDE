using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

/// <summary>How the last build went (Build, Rebuild, Clean, or the build before Run / Debug).</summary>
public sealed record BuildOutcome(DateTime Finished, string What, bool Succeeded, int Errors, int Warnings);

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
    private readonly Services.Settings.IEditorSettings _settings;
    private readonly IMessenger _messenger;
    private bool _runStopRequested;

    public RunViewModel(
        WorkspaceModel workspace,
        WorkspaceViewModel explorer,
        DocumentManager documents,
        DocumentSaveService saver,
        IDialogService dialogs,
        Services.Settings.IEditorSettings settings,
        IMessenger messenger)
    {
        _settings = settings;
        settings.PropertyChanged += OnSettingsChanged;
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

    /// <summary>The last build's result this session, or null before the first.</summary>
    public BuildOutcome? LastBuild { get; private set; }

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

    /// <summary>
    /// Claude's build (the MCP build tool): what Build builds, except that it never asks which project and leaves
    /// unsaved files alone (the user's half-typed edits aren't Claude's to save). Null when a build or run is already
    /// going, or there's nothing to build.
    /// </summary>
    public async Task<BuildOutcome?> BuildForAgentAsync()
    {
        if (IsBusy || (_explorer.SolutionPath ?? StartupProjectPath) is not { } path)
            return null;
        var before = LastBuild;
        await BuildAsync(path, BuildKind.Build, save: false);
        return ReferenceEquals(LastBuild, before) ? null : LastBuild;
    }

    private async Task BuildAsync(string path, BuildKind kind = BuildKind.Build, bool save = true)
    {
        // Like other IDEs: save everything before building (a clean doesn't read the sources).
        if (IsBusy || save && kind != BuildKind.Clean && !await _saver.SaveAllAsync())
            return;

        var configuration = Configuration;
        var (doing, done) = kind switch
        {
            BuildKind.Rebuild => ("Rebuilding", "Rebuild"),
            BuildKind.Clean => ("Cleaning", "Clean"),
            _ => ("Building", "Build")
        };

        BeginBuildOutput($"{doing} {Path.GetFileName(path)} ({configuration})");
        IsBusy = true;
        try
        {
            var exitCode = await BuildWithNoticeAsync(() => kind switch
            {
                BuildKind.Rebuild => _projectRunner.RebuildAsync(path, configuration),
                BuildKind.Clean => _projectRunner.CleanAsync(path, configuration),
                _ => _projectRunner.BuildAsync(path, configuration)
            }, done);
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
            var buildExitCode = await BuildWithNoticeAsync(() => _projectRunner.BuildAsync(projectPath, configuration));
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


    /// <summary>
    /// Runs a build, telling the rest of the app (the previews) when it starts and finishes, and putting its errors
    /// and warnings in the Problems list (shown when the build failed, as Visual Studio shows the Error List).
    /// </summary>
    private async Task<int> BuildWithNoticeAsync(Func<Task<int>> build, string what = "Build")
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
            PublishBuildProblems(exitCode, what);
        }
    }

    /// <summary>The raw lines of the build going on (every thread), for the parser once it's done.</summary>
    private readonly List<string> _buildLines = [];

    private void PublishBuildProblems(int exitCode, string what)
    {
        string[] lines;
        lock (_buildLines)
            lines = [.. _buildLines];
        var diagnostics = BuildOutputParser.Parse(lines);
        _messenger.Send(new BuildProblemsMessage(diagnostics));

        var errors = diagnostics.Count(d => d.IsError);
        var warnings = diagnostics.Count - errors;
        LastBuild = new BuildOutcome(DateTime.Now, what, exitCode == 0, errors, warnings);
        ShowStatus(exitCode == 0
            ? warnings > 0 ? $"{what} succeeded with {warnings} warning{(warnings == 1 ? "" : "s")}." : $"{what} succeeded."
            : errors > 0 ? $"{what} failed: {errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}." : $"{what} failed.");
        if (errors > 0)
            _messenger.Send(new ShowToolTabMessage(ToolTab.Problems));
    }

    /// <summary>Nothing built any more (a Clean, or the workspace closed): no build problems either.</summary>
    private void ClearBuildProblems() => _messenger.Send(new BuildProblemsMessage([]));

    /// <summary>Double-click on an Output line that is an error or warning: open the file at its line, as in VS.</summary>
    [RelayCommand]
    private void OpenOutputLine(string? line)
    {
        if (line is null || BuildOutputParser.TryParseLine(line) is not { HasLocation: true } diagnostic)
            return;
        _messenger.Send(new NavigateToProblemMessage(new ProblemItem(diagnostic.Path, diagnostic.Severity, diagnostic.Line,
            diagnostic.Column, diagnostic.Message, ProblemSource.Build, diagnostic.Code, diagnostic.Project)));
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
        lock (_buildLines)
            _buildLines.Clear();
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
        {
            AppendConsoleOutput(message);
            return;
        }

        lock (_buildLines)
            _buildLines.Add(message);
        AppendBuildOutput(message);
    }

    public void Dispose()
    {
        _debugSession?.Dispose();
        _debugSession = null;
        _projectRunner.Dispose();
    }
}
