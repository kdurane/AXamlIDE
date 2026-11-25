using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The debug half of RunViewModel: the netcoredbg session, the stopped/running state, and
/// keeping breakpoints in sync with open documents. A candidate to become its own
/// DebugViewModel as the debugger grows (call stack, variables, watches).
/// </summary>
public partial class RunViewModel
{
    private DebugSession? _debugSession;
    private string? _debugProjectPath;   // what Restart rebuilds and relaunches
    private int _stoppedThreadId = 1;
    private readonly Dictionary<OpenDocument, Action> _breakpointHandlers = [];

    /// <summary>Paused at a breakpoint or step: Continue and the Step commands are only available then.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueDebuggingCommand), nameof(StepOverCommand),
        nameof(StepIntoCommand), nameof(StepOutCommand), nameof(PauseDebuggingCommand))]
    public partial bool IsDebugStopped { get; set; }

    /// <summary>What the debugger is doing ("Paused (breakpoint).", "Stepping..."); shown in the status bar.</summary>
    [ObservableProperty] public partial string DebugStatus { get; set; } = "No active debug session.";

    partial void OnDebugStatusChanged(string value) => ShowStatus(value);

    public bool IsDebugging => _debugSession is not null;

    private DebugInspector? _inspector;
    private bool _hasStopped;

    // ---------------------------------------------------------------- start

    [RelayCommand]
    private async Task StartDebuggingAsync(ProjectTreeNode? selection)
    {
        if (IsBusy || IsRunning || await ChooseStartupProjectAsync(selection) is not { } projectPath)
            return;

        await DebugProjectAsync(projectPath);
    }

    /// <summary>F5: continue when paused, otherwise start debugging.</summary>
    [RelayCommand]
    private Task StartOrContinueDebuggingAsync(ProjectTreeNode? selection)
    {
        if (!IsDebugging)
            return StartDebuggingCommand.ExecuteAsync(selection);
        return IsDebugStopped ? ContinueDebuggingCommand.ExecuteAsync(null) : Task.CompletedTask;
    }

    /// <summary>Stop the current debug session and start a new one for the same project (rebuilding first).</summary>
    [RelayCommand(CanExecute = nameof(IsDebugging))]
    private async Task RestartDebuggingAsync()
    {
        if (_debugSession is not { } session || _debugProjectPath is not { } projectPath)
            return;

        await StopDebugSessionAsync(session);
        await DebugProjectAsync(projectPath);
    }

    private async Task DebugProjectAsync(string projectPath)
    {
        if (IsBusy || IsRunning)
            return;

        if (!await _saver.SaveAllAsync())   // the build must match the editor
            return;

        IsBusy = true;
        var configuration = Configuration;
        BeginBuildOutput($"Building {Path.GetFileName(projectPath)} ({configuration}) before debugging");
        try
        {
            var buildExitCode = await BuildWithNoticeAsync(() => _projectRunner.BuildAsync(projectPath, configuration));

            if (buildExitCode != 0)
            {
                AppendBuildOutput($"Debugging cancelled because the build failed with exit code {buildExitCode}.");
                return;
            }

            var (programPath, workingDirectory) = await Task.Run(() => ResolveProgram(projectPath, configuration));
            var (adapter, note) = DebugAdapters.Choose(_settings.Features.Debugger);
            if (note is not null)
                ShowStatus(note);
            var programName = Path.GetFileNameWithoutExtension(programPath);
            var session = new DebugSession(adapter);
            if (adapter.RunsInTerminal)   // the program gets the Run panel's terminal: it can read input
                session.RunInTerminal = launch => Dispatcher.UIThread.InvokeAsync(
                    () => StartInTerminalAsync(launch, $"{programName} ({configuration}, debugging)"));
            _debugSession = session;
            _debugProjectPath = projectPath;
            _hasStopped = false;
            var inspector = new DebugInspector(session);
            _inspector = inspector;
            inspector.Output += AppendConsoleOutput;          // exceptions thrown, and stops it couldn't read, go to the console pane
            inspector.Stopped += snapshot => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_debugSession, session))
                    return;
                _stoppedThreadId = snapshot.ThreadId;
                IsDebugStopped = true;
                if (!_hasStopped)
                {
                    _hasStopped = true;   // the first stop of a session brings up Locals (VS shows its debugger windows)
                    _messenger.Send(new ShowToolTabMessage(ToolTab.Locals));
                }
                DebugStatus = $"Paused ({snapshot.Reason}).";
                if (snapshot.Top is { Path: { } path } top && File.Exists(path))
                    _messenger.Send(new NavigateToDebugLocationMessage(path, top.Line)); // navigate first so the doc is open...
                _messenger.Send(new DebugSnapshotChangedMessage(snapshot));                // ...then show the values
            });
            inspector.Resumed += () => Dispatcher.UIThread.Post(() => _messenger.Send(new DebugSnapshotChangedMessage(null)));
            OnIsDebuggingChanged();
            IsRunning = true;
            IsDebugStopped = false;
            DebugStatus = $"Starting {Path.GetFileName(programPath)} ({configuration}, {adapter.Name})...";
            ConsoleOutput.Clear();   // a fresh session starts with an empty Console, as a run does (and VS's Debug pane)
            _messenger.Send(new ShowToolTabMessage(ToolTab.Console));
            session.OutputReceived += AppendConsoleOutput;
            session.EventReceived += message => OnDebugAdapterEvent(session, message);
            session.Failed += error => Dispatcher.UIThread.Post(() =>
                EndDebugSession(session, $"Debugger failed: {error.Message}"));

            var breakpoints = _documents.Documents
                .Where(document => document is { LanguageId: LanguageIds.CSharp, BreakpointLines.Count: > 0 })
                .ToDictionary(
                    document => document.Path,
                    document => document.DebuggerBreakpoints,
                    PathComparer);
            foreach (var (path, _) in _closedBreakpoints.All)   // a closed file's breakpoints still stop, as in VS
                if (IsCSharpFile(path) && !breakpoints.ContainsKey(path) && BreakpointsFor(path) is { Count: > 0 } closed)
                    breakpoints[path] = closed;

            _sentExceptionFilters = ExceptionFilterList();
            await session.StartAsync(programPath, workingDirectory, breakpoints, _sentExceptionFilters);
            if (ReferenceEquals(_debugSession, session))
                DebugStatus = $"Debugging ({configuration}, {adapter.Name}). Execution will pause at enabled breakpoints.";
        }
        catch (Exception ex)
        {
            if (_debugSession is { } session)
                EndDebugSession(session, $"Couldn't start debugging: {ex.Message}");
            else
                ShowBuildMessage($"Couldn't start debugging: {ex.Message}");
            AppendConsoleOutput($"Debug launch failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (string ProgramPath, string WorkingDirectory) ResolveProgram(string projectPath, string configuration)
    {
        var facts = _workspace.Host.GetProjectFacts(projectPath, configuration);
        var assemblyName = facts.AssemblyName;
        var outputPath = facts.OutputPath;
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Project '{projectPath}' has no parent directory.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException($"MSBuild did not provide an output path for '{projectPath}'.");

        var outputDirectory = Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.GetFullPath(Path.Combine(projectDirectory, outputPath));
        var programPath = Path.Combine(outputDirectory, assemblyName + ".dll");

        if (!File.Exists(programPath))
            throw new FileNotFoundException(
                $"The built application assembly wasn't found at '{programPath}'. Build the project first.",
                programPath);

        return (programPath, projectDirectory);
    }

    // ------------------------------------------------------ continue / stop

    [RelayCommand(CanExecute = nameof(IsDebugStopped))]
    private async Task ContinueDebuggingAsync()
    {
        if (_debugSession is null || !IsDebugStopped)
            return;
        try
        {
            await _debugSession.ContinueAsync(_stoppedThreadId);
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't continue debugging: {ex.Message}");
        }
    }

    private bool CanPause => IsDebugging && !IsDebugStopped;

    /// <summary>Break into the running debuggee (DAP "pause"); the adapter then reports a stop like a breakpoint.</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseDebuggingAsync()
    {
        if (_debugSession is not { } session)
            return;
        try
        {
            DebugStatus = "Pausing...";   // before the request: the "stopped" reply can arrive first
            var threads = await session.GetThreadIdsAsync();
            await session.PauseAsync(threads.Count > 0 ? threads[0] : _stoppedThreadId);
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't pause: {ex.Message}");
        }
    }

    /// <summary>The toolbar's play/pause button: continue when paused, pause when running.</summary>
    [RelayCommand(CanExecute = nameof(IsDebugging))]
    private Task ContinueOrPauseDebuggingAsync()
        => IsDebugStopped ? ContinueDebuggingCommand.ExecuteAsync(null) : PauseDebuggingCommand.ExecuteAsync(null);

    [RelayCommand(CanExecute = nameof(IsDebugStopped))] private Task StepOverAsync() => StepAsync(s => s.StepOverAsync(_stoppedThreadId), "step over");
    [RelayCommand(CanExecute = nameof(IsDebugStopped))] private Task StepIntoAsync() => StepAsync(s => s.StepIntoAsync(_stoppedThreadId), "step into");
    [RelayCommand(CanExecute = nameof(IsDebugStopped))] private Task StepOutAsync() => StepAsync(s => s.StepOutAsync(_stoppedThreadId), "step out");

    private async Task StepAsync(Func<DebugSession, Task> step, string what)
    {
        if (_debugSession is not { } session || !IsDebugStopped)
            return;
        // netcoredbg doesn't reliably send "continued" for steps, so flip state ourselves.
        IsDebugStopped = false;
        DebugStatus = "Stepping...";
        _messenger.Send(new DebugSnapshotChangedMessage(null));
        try
        {
            await step(session);
        }
        catch (Exception ex)
        {
            IsDebugStopped = true;
            ShowStatus($"Couldn't {what}: {ex.Message}");
        }
    }

    /// <summary>Stop the debug session (if there is one) and wait until it has ended: before the workspace or the app closes.</summary>
    public Task StopDebuggingAsync() => _debugSession is { } session ? StopDebugSessionAsync(session) : Task.CompletedTask;

    private async Task StopDebugSessionAsync(DebugSession session)
    {
        DebugStatus = "Stopping debug session...";
        try
        {
            await session.StopAsync();
        }
        catch (Exception ex)
        {
            AppendConsoleOutput($"Couldn't stop debugger cleanly: {ex.Message}");
        }
        finally
        {
            EndDebugSession(session, "Debug session stopped.");
        }
    }

    private void EndDebugSession(DebugSession session, string message)
    {
        if (!ReferenceEquals(_debugSession, session))
            return;

        _debugSession = null;
        _inspector?.Detach();
        _inspector = null;
        _messenger.Send(new DebugSnapshotChangedMessage(null));
        session.Dispose();
        if (session.RunInTerminal is not null)
            _runTerminal.Stop();   // the program in the Run panel goes with the session
        OnIsDebuggingChanged();
        IsDebugStopped = false;
        IsRunning = false;
        DebugStatus = message;
        AppendConsoleOutput(message);
    }

    // --------------------------------------------------- adapter event handling

    private void OnDebugAdapterEvent(DebugSession session, JsonElement message)
    {
        if (!message.TryGetProperty("event", out var eventName))
            return;

        switch (eventName.GetString())
        {
            case "output":
                if (message.TryGetProperty("body", out var outputBody)
                    && outputBody.TryGetProperty("output", out var output))
                    AppendConsoleOutput(output.GetString() ?? string.Empty);
                break;
            case "continued":
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_debugSession, session))
                    {
                        IsDebugStopped = false;
                        DebugStatus = "Debugging.";
                    }
                });
                break;
            case "process":
                if (message.TryGetProperty("body", out var processBody)
                    && processBody.TryGetProperty("systemProcessId", out var pid) && pid.TryGetInt32(out var processId))
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_debugSession, session))
                            _messenger.Send(new DebuggeeStartedMessage(processId));
                    });
                break;
            case "terminated":
                Dispatcher.UIThread.Post(() => EndDebugSession(session, "Debug session ended."));
                break;
            case "exited":
                var code = message.TryGetProperty("body", out var exitedBody)
                           && exitedBody.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : -1;
                AppendConsoleOutput($"Debuggee exited with code {code}.");
                break;
        }
    }

    // ------------------------------------------------------------ breakpoints

    // Subscribed to DocumentManager.Opened / Closing in the constructor.
    private void TrackDocument(OpenDocument document)
    {
        void handler() => OnDocumentBreakpointsChanged(document);
        _breakpointHandlers[document] = handler;
        document.BreakpointsChanged += handler;
    }

    private void UntrackDocument(OpenDocument document)
    {
        if (_breakpointHandlers.Remove(document, out var handler))
            document.BreakpointsChanged -= handler;
        // Once it has closed: its breakpoints may have moved to ClosedFileBreakpoints (they stay), or gone with it.
        if (document.LanguageId == LanguageIds.CSharp)
            Dispatcher.UIThread.Post(() => OnFileBreakpointsChanged(document.Path));
    }

    /// <summary>A file's breakpoints changed outside its open document (it closed or opened, or the list changed them).</summary>
    private void OnFileBreakpointsChanged(string path)
    {
        if (_debugSession is { } session && IsCSharpFile(path))
            _ = UpdateDebugBreakpointsAsync(session, path, BreakpointsFor(path));
    }

    /// <summary>What the debugger should have for a file: its open document's enabled breakpoints, else its closed ones.</summary>
    private IReadOnlyList<SourceBreakpoint> BreakpointsFor(string path)
        => _documents.Documents.FirstOrDefault(document => PathComparer.Equals(document.Path, path)) is { } open
            ? open.DebuggerBreakpoints
            : [.. _closedBreakpoints.For(path).Where(b => b.IsEnabled).Select(b => new SourceBreakpoint(b.Line, b.Condition, b.HitCondition))];

    private static bool IsCSharpFile(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private void OnDocumentBreakpointsChanged(OpenDocument document)
    {
        if (_debugSession is not { } session || document.LanguageId != LanguageIds.CSharp)
            return;
        _ = UpdateDebugBreakpointsAsync(session, document.Path, document.DebuggerBreakpoints);
    }

    private async Task UpdateDebugBreakpointsAsync(DebugSession session, string path, IReadOnlyList<SourceBreakpoint> breakpoints)
    {
        try
        {
            await session.SetBreakpointsAsync(path, breakpoints);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowStatus(
                $"Couldn't update breakpoints for '{Path.GetFileName(path)}': {ex.Message}"));
        }
    }

    // ---------------------------------------------------------------- exceptions

    /// <summary>Settings > Debugging > Exceptions, as the adapter's filters.</summary>
    private string[] ExceptionFilterList()
        => [_settings.Features.BreakOnAllExceptions ? ExceptionFilters.All : ExceptionFilters.UserUnhandled];

    /// <summary>The exceptions setting changed: tell a running session at once.</summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Services.Settings.IEditorSettings.Features) || _debugSession is not { } session)
            return;
        var filters = ExceptionFilterList();
        if (filters.SequenceEqual(_sentExceptionFilters))
            return;
        _sentExceptionFilters = filters;
        _ = UpdateExceptionFiltersAsync(session, filters);
    }

    private string[] _sentExceptionFilters = [];

    private async Task UpdateExceptionFiltersAsync(DebugSession session, string[] filters)
    {
        try
        {
            await session.SetExceptionBreakpointsAsync(filters);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowStatus($"Couldn't change the exception settings: {ex.Message}"));
        }
    }

    // ---------------------------------------------------------------- helpers

    private void OnIsDebuggingChanged()
    {
        OnPropertyChanged(nameof(IsDebugging));
        _messenger.Send(new DebugSessionChangedMessage(IsDebugging));
        RestartDebuggingCommand.NotifyCanExecuteChanged();
        PauseDebuggingCommand.NotifyCanExecuteChanged();
        ContinueOrPauseDebuggingCommand.NotifyCanExecuteChanged();
    }

    private void ShowStatus(string text) => _messenger.Send(new StatusMessage(text));

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
