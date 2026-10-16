using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The debug half of RunViewModel: the netcoredbg session, the stopped/running state, and
/// keeping breakpoints in sync with open documents. This is the seam where a DebugViewModel
/// will be carved out when the real debugger work starts (step, pause, call stack, variables,
/// hover values). Bodies are the old MainWindowViewModel code.
/// </summary>
public partial class RunViewModel
{
    private NetCoreDbgSession? _debugSession;
    private string? _debugProjectPath;
    private int _stoppedThreadId = 1;
    private readonly Dictionary<OpenDocument, Action> _breakpointHandlers = [];

    [ObservableProperty] public partial bool IsDebugStopped { get; set; }
    [ObservableProperty] public partial string DebugStatus { get; set; } = "No active debug session.";

    public bool IsDebugging => _debugSession is not null;

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
            _debugProjectPath = projectPath;
            OnPropertyChanged(nameof(IsDebugging));
            IsRunning = true;
            IsDebugStopped = false;
            DebugStatus = $"Starting {Path.GetFileName(programPath)}...";
            ToolTabRequested?.Invoke(ToolTab.Debug);
            session.OutputReceived += AppendConsoleOutput;
            session.EventReceived += message => OnDebugAdapterEvent(session, message);
            session.Failed += error => Dispatcher.UIThread.Post(() =>
                EndDebugSession(session, $"Debugger failed: {error.Message}"));

            var projects = await Task.Run(() => GetProjectClosure(projectPath));

            var breakpoints = _documents
                .Where(document => document is { LanguageId: "csharp", BreakpointLines.Count: > 0 }
                                   && _workspace.FindProjectForDocument(document.Path) is { } p
                                   && projects.Contains(p))
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

    private HashSet<string> GetProjectClosure(string rootProject)
    {
        var result = new HashSet<string>(PathComparer);
        var stack = new Stack<string>();
        stack.Push(rootProject);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current)) continue;
            foreach (var reference in _workspace.Host.GetProjectReferences(current))
            {
                // may need the same backslash fix, and resolving relative to current's directory
                var normalized = reference.Replace('\\', Path.DirectorySeparatorChar);
                var full = Path.IsPathRooted(normalized)
                    ? normalized
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, normalized));
                stack.Push(full);
            }
        }
        return result;
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

        // Was a Console.WriteLine that let the debugger start and then fail later, less clearly.
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
        _debugProjectPath = null;
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

        // NOTE: this dumps every raw adapter message into the Console tab. Useful while the
        // debugger is a proof of concept; it should go when the real debug UI lands.
        AppendConsoleOutput(message.GetRawText());

        switch (eventName.GetString())
        {
            case "output":
                if (message.TryGetProperty("body", out var outputBody)
                    && outputBody.TryGetProperty("output", out var output))
                    AppendConsoleOutput(output.GetString() ?? string.Empty);
                break;
            case "stopped":
                if (!message.TryGetProperty("body", out var stoppedBody))
                    return;
                var reason = stoppedBody.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString() ?? "breakpoint"
                    : "breakpoint";
                var threadId = stoppedBody.TryGetProperty("threadId", out var threadElement)
                    ? threadElement.GetInt32()
                    : 1;
                _stoppedThreadId = threadId;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_debugSession, session))
                        return;
                    IsDebugStopped = true;
                    DebugStatus = $"Paused ({reason}).";
                });
                _ = NavigateToTopFrameAsync(session, threadId);
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

    private async Task NavigateToTopFrameAsync(NetCoreDbgSession session, int threadId)
    {
        try
        {
            var frame = await session.GetTopFrameAsync(threadId);
            if (frame is { } location && File.Exists(location.Path))
                Dispatcher.UIThread.Post(() => DebugLocationChanged?.Invoke(location.Path, location.Line));
        }
        catch (Exception ex)
        {
            AppendConsoleOutput($"Couldn't retrieve the stopped location: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ breakpoints

    /// <summary>Call when a document is opened (was part of MainWindowViewModel.OnDocumentOpened).</summary>
    public void TrackDocument(OpenDocument document)
    {
        Action handler = () => OnDocumentBreakpointsChanged(document);
        _breakpointHandlers[document] = handler;
        document.BreakpointsChanged += handler;
    }

    /// <summary>Call when a document is closing (was part of MainWindowViewModel.OnDocumentClosing).</summary>
    public void UntrackDocument(OpenDocument document)
    {
        if (_breakpointHandlers.Remove(document, out var handler))
            document.BreakpointsChanged -= handler;
        if (_debugSession is { } session && document.LanguageId == "csharp")
            _ = UpdateDebugBreakpointsAsync(session, document.Path, []);
    }

    private void OnDocumentBreakpointsChanged(OpenDocument document)
    {
        if (_debugSession is not { } session || document.LanguageId != "csharp")
            return;
        if (_debugProjectPath is null
            || !PathsEqual(_workspace.FindProjectForDocument(document.Path), _debugProjectPath))
            return;

        _ = UpdateDebugBreakpointsAsync(session, document.Path, document.BreakpointLines);
    }

    private async Task UpdateDebugBreakpointsAsync(
        NetCoreDbgSession session,
        string path,
        IReadOnlyList<int> lines)
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

    private static bool PathsEqual(string? left, string? right)
        => left is not null && right is not null && PathComparer.Equals(left, right);
}
