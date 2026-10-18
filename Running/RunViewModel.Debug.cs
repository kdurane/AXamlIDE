using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using AXamlIDE.Debugging;
using AXamlIDE.Documents;
using AXamlIDE.Lsp;
using AXamlIDE.Shell;
using AXamlIDE.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.Running;

/// <summary>
/// The debug half of RunViewModel: the netcoredbg session, the stopped/running state, and
/// keeping breakpoints in sync with open documents. A candidate to become its own
/// DebugViewModel as the debugger grows (call stack, variables, watches).
/// </summary>
public partial class RunViewModel
{
    private NetCoreDbgSession? _debugSession;
    private int _stoppedThreadId = 1;
    private readonly Dictionary<OpenDocument, Action> _breakpointHandlers = [];

    [ObservableProperty] public partial bool IsDebugStopped { get; set; }
    [ObservableProperty] public partial string DebugStatus { get; set; } = "No active debug session.";

    public bool IsDebugging => _debugSession is not null;

    private DebugInspector? _inspector;

    /// <summary>Snapshot at each stop (null when running/ended) - the editor uses it for inline values.</summary>
    public event Action<StopSnapshot?>? DebugSnapshotChanged;

    /// <summary>The debuggee stopped at this file and line (the shell navigates the editor there).</summary>
    public event Action<string, int>? DebugLocationChanged;

    // ---------------------------------------------------------------- start

    [RelayCommand]
    private async Task StartDebuggingAsync(ProjectTreeNode? selection)
    {
        var selected = selection ?? _getSelectedNode();
        string? projectPath;
        try
        {
            projectPath = ResolveStartupProject(selected);
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

        if (IsBusy || IsRunning)
            return;

        if (_documents.Any(document => document.IsDirty))
        {
            _showStatus("Save open document changes before debugging so the built code matches the editor.");
            return;
        }

        IsBusy = true;
        BeginBuildOutput($"Building {Path.GetFileName(projectPath)} (Debug) before debugging");
        try
        {
            var buildExitCode = await _projectRunner.BuildAsync(projectPath);
            if (buildExitCode != 0)
            {
                AppendBuildOutput($"Debugging cancelled because the build failed with exit code {buildExitCode}.");
                return;
            }

            var (programPath, workingDirectory) = await Task.Run(() => ResolveDebugProgram(projectPath));
            var session = new NetCoreDbgSession(NetCoreDbgSession.ResolveExecutablePath());
            _debugSession = session;
            var inspector = new DebugInspector(session);
            _inspector = inspector;
            inspector.Output += AppendConsoleOutput;          // stack trace goes to the console pane
            inspector.Stopped += snapshot => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_debugSession, session))
                    return;
                _stoppedThreadId = snapshot.ThreadId;
                IsDebugStopped = true;
                DebugStatus = $"Paused ({snapshot.Reason}).";
                if (snapshot.Top is { Path: { } path } top && File.Exists(path))
                    DebugLocationChanged?.Invoke(path, top.Line);   // navigate first so the doc is open...
                DebugSnapshotChanged?.Invoke(snapshot);             // ...then show the values
            });
            inspector.Resumed += () => Dispatcher.UIThread.Post(() => DebugSnapshotChanged?.Invoke(null));
            OnPropertyChanged(nameof(IsDebugging));
            IsRunning = true;
            IsDebugStopped = false;
            DebugStatus = $"Starting {Path.GetFileName(programPath)}...";
            ToolTabRequested?.Invoke(ToolTab.Debug);
            session.OutputReceived += AppendConsoleOutput;
            session.EventReceived += message => OnDebugAdapterEvent(session, message);
            session.Failed += error => Dispatcher.UIThread.Post(() =>
                EndDebugSession(session, $"Debugger failed: {error.Message}"));

            var breakpoints = _documents
                .Where(document => document is { LanguageId: LanguageIds.CSharp, BreakpointLines.Count: > 0 })
                .ToDictionary(
                    document => document.Path,
                    document => document.BreakpointLines,
                    PathComparer);

            await session.StartAsync(programPath, workingDirectory, breakpoints);
            if (ReferenceEquals(_debugSession, session))
                DebugStatus = "Debugging. Execution will pause at enabled breakpoints.";
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

    private (string ProgramPath, string WorkingDirectory) ResolveDebugProgram(string projectPath)
    {
        var assemblyName = _workspace.Host.GetProjectAssemblyName(projectPath);
        var outputPath = _workspace.Host.GetProjectOutputPath(projectPath);
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

    [RelayCommand]
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
            _showStatus($"Couldn't continue debugging: {ex.Message}");
        }
    }

    [RelayCommand] private Task StepOverAsync() => StepAsync(s => s.StepOverAsync(_stoppedThreadId), "step over");
    [RelayCommand] private Task StepIntoAsync() => StepAsync(s => s.StepIntoAsync(_stoppedThreadId), "step into");
    [RelayCommand] private Task StepOutAsync() => StepAsync(s => s.StepOutAsync(_stoppedThreadId), "step out");

    private async Task StepAsync(Func<NetCoreDbgSession, Task> step, string what)
    {
        if (_debugSession is not { } session || !IsDebugStopped)
            return;
        // netcoredbg doesn't reliably send "continued" for steps, so flip state ourselves.
        IsDebugStopped = false;
        DebugStatus = "Stepping...";
        DebugSnapshotChanged?.Invoke(null);
        try
        {
            await step(session);
        }
        catch (Exception ex)
        {
            IsDebugStopped = true;
            _showStatus($"Couldn't {what}: {ex.Message}");
        }
    }

    private async Task StopDebugSessionAsync(NetCoreDbgSession session)
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

    private void EndDebugSession(NetCoreDbgSession session, string message)
    {
        if (!ReferenceEquals(_debugSession, session))
            return;

        _debugSession = null;
        _inspector?.Detach();
        _inspector = null;
        DebugSnapshotChanged?.Invoke(null);
        session.Dispose();
        OnPropertyChanged(nameof(IsDebugging));
        IsDebugStopped = false;
        IsRunning = false;
        DebugStatus = message;
        AppendConsoleOutput(message);
    }

    // --------------------------------------------------- adapter event handling

    private void OnDebugAdapterEvent(NetCoreDbgSession session, JsonElement message)
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

    /// <summary>Call when a document is opened, so its breakpoint changes reach the debugger.</summary>
    public void TrackDocument(OpenDocument document)
    {
        Action handler = () => OnDocumentBreakpointsChanged(document);
        _breakpointHandlers[document] = handler;
        document.BreakpointsChanged += handler;
    }

    /// <summary>Call when a document is closing.</summary>
    public void UntrackDocument(OpenDocument document)
    {
        if (_breakpointHandlers.Remove(document, out var handler))
            document.BreakpointsChanged -= handler;
        if (_debugSession is { } session && document.LanguageId == LanguageIds.CSharp)
            _ = UpdateDebugBreakpointsAsync(session, document.Path, []);
    }

    private void OnDocumentBreakpointsChanged(OpenDocument document)
    {
        if (_debugSession is not { } session || document.LanguageId != LanguageIds.CSharp)
            return;
        _ = UpdateDebugBreakpointsAsync(session, document.Path, document.BreakpointLines);
    }

    private async Task UpdateDebugBreakpointsAsync(NetCoreDbgSession session, string path, IReadOnlyList<int> lines)
    {
        try
        {
            await session.SetBreakpointsAsync(path, lines);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _showStatus(
                $"Couldn't update breakpoints for '{Path.GetFileName(path)}': {ex.Message}"));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
