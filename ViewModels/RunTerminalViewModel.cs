using System;
using System.ComponentModel;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One start of a program in the Run terminal: what to run, and when it started (its process id) and ended (its
/// exit code; null when the terminal couldn't tell).
/// </summary>
public sealed class TerminalRun(TerminalLaunch launch)
{
    private readonly TaskCompletionSource<int?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalLaunch Launch { get; } = launch;

    /// <summary>The process id once it's running; null if it couldn't be started.</summary>
    public Task<int?> Started => _started.Task;

    /// <summary>Its exit code once it has ended (null: unknown, or it never started).</summary>
    public Task<int?> Exited => _exited.Task;

    public void OnStarted(int? processId)
    {
        _started.TrySetResult(processId);
        if (processId is null)
            _exited.TrySetResult(null);
    }

    public void OnExited(int? exitCode)
    {
        _started.TrySetResult(null);
        _exited.TrySetResult(exitCode);
    }
}

/// <summary>
/// The Run panel: the program the IDE started (Start Without Debugging, or Debug with a debugger that can run it in a
/// terminal), in a real terminal, as VS Code runs it in its integrated terminal and Rider in its Run window. The program
/// gets a proper console: it can read the keyboard (Console.ReadLine, ReadKey), and colours and cursor movement show.
/// The view's TerminalControl runs it; this says what to run and passes back when it started and ended. Starting a
/// program doesn't take the keyboard focus: click in the panel to type to it.
/// </summary>
public sealed partial class RunTerminalViewModel : ObservableObject
{
    private readonly IEditorSettings _settings;
    private TerminalRun? _pending;   // asked for before the panel's view existed

    public RunTerminalViewModel(IEditorSettings settings)
    {
        _settings = settings;
        settings.PropertyChanged += OnSettingsChanged;
    }

    /// <summary>The editor's font without ligatures, as the Terminal panel uses.</summary>
    public string FontFamily => EditorSettings.ResolveFontFamily(_settings.EditorFontFamily, ligatures: false);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IEditorSettings.EditorFontFamily))
            OnPropertyChanged(nameof(FontFamily));
    }

    /// <summary>What's running, as the panel's header shows it: "Contoso.App (Debug)".</summary>
    [ObservableProperty] public partial string Title { get; private set; } = "Nothing has been run yet.";

    /// <summary>The view should start this run (stopping what's running).</summary>
    public event Action<TerminalRun>? LaunchRequested;

    /// <summary>The view should stop the running program.</summary>
    public event Action? StopRequested;

    /// <summary>
    /// Start a program in the terminal. Show the Run panel first (ShowToolTabMessage) so its view exists; if it doesn't
    /// yet, the run starts as soon as it does.
    /// </summary>
    public TerminalRun Start(TerminalLaunch launch, string title)
    {
        _pending?.OnStarted(null);   // replaced before it started
        var run = new TerminalRun(launch);
        Title = title;
        if (LaunchRequested is { } launchRequested)
            launchRequested(run);
        else
            _pending = run;
        return run;
    }

    /// <summary>A run asked for before the view existed (the view starts it).</summary>
    public TerminalRun? TakePendingRun()
    {
        var run = _pending;
        _pending = null;
        return run;
    }

    public void Stop()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            pending.OnStarted(null);
        }
        StopRequested?.Invoke();
    }
}
