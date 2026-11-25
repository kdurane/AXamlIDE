using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AXamlIDE.ViewModels;
using Iciclecreek.Terminal;

namespace AXamlIDE.Views;

/// <summary>
/// The Run panel. DataContext: <see cref="RunTerminalViewModel"/>, which says what to run; the TerminalControl runs it
/// and this tells the run when it started and ended. Colours follow the IDE's theme, as the Terminal panel's do.
/// </summary>
public partial class RunTerminalView : UserControl
{
    private RunTerminalViewModel? _viewModel;
    private TerminalRun? _current;
    private bool _templateApplied;
    // Done once the control's terminal exists (its template is applied). A run can be asked for before that: the Run
    // panel is brought forward at the same moment, and it lays out on the next pass.
    private readonly TaskCompletionSource _terminalReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunTerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Terminal.ProcessExited += OnProcessExited;
        Terminal.WritingMessage += OnWritingMessage;
        Terminal.TemplateApplied += (_, _) =>
        {
            _templateApplied = true;
            TerminalPalette.Apply(Terminal);
            Terminal.BeginReparent();   // keep the program running when Dock moves the panel (see TerminalView)
            _terminalReady.TrySetResult();
        };
        Terminal.ActualThemeVariantChanged += (_, _) =>
        {
            if (_templateApplied)
                TerminalPalette.Apply(Terminal);
        };
        if (Terminal.ContextMenu?.Items is { Count: > 1 } items)
        {
            ((MenuItem)items[0]!).InputGesture = TerminalKeyGestures.Copy[0];
            ((MenuItem)items[1]!).InputGesture = TerminalKeyGestures.Paste[0];
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested -= Launch;
            _viewModel.StopRequested -= Stop;
        }

        _viewModel = DataContext as RunTerminalViewModel;
        if (_viewModel is null)
            return;
        _viewModel.LaunchRequested += Launch;
        _viewModel.StopRequested += Stop;
        if (_viewModel.TakePendingRun() is { } pending)
            Launch(pending);
    }

    private async void Launch(TerminalRun run)
    {
        Stop();
        _current?.OnExited(null);
        _current = run;
        var launch = run.Launch;
        try
        {
            // A debugger waits for the process id, so give up rather than leave it waiting on a panel that never shows.
            await _terminalReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            run.OnStarted(null);
            return;
        }
        if (!ReferenceEquals(_current, run))
            return;   // another run replaced this one while the panel was getting ready
        Terminal.ClearScreen();

        var environment = new System.Collections.Generic.Dictionary<string, string> { ["TERM_PROGRAM"] = "AXamlIDE" };
        foreach (var (name, value) in launch.Environment)
            if (value is not null)
                environment[name] = value;
        Terminal.EnvironmentVariables = environment;
        try
        {
            await Terminal.LaunchProcess(launch.Folder, launch.CommandLine[0], [.. launch.CommandLine.Skip(1)]);
            run.OnStarted(Terminal.Pid > 0 ? Terminal.Pid : null);   // a quick program may already have ended
        }
        catch (Exception ex)
        {
            run.OnStarted(null);   // first: the debugger is waiting for the answer
            if (_templateApplied)   // the control's Terminal getter throws until then
                Terminal.Terminal.Write($"Couldn't start {launch.CommandLine[0]}: {ex.Message}\r\n");
        }
    }

    private void Stop()
    {
        if (Terminal.IsLive)
            Terminal.Kill();
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e)
        => _current?.OnExited(e.ExitCodeKnown ? e.ExitCode : null);

    /// <summary>VS Code's wording when the program ends.</summary>
    private void OnWritingMessage(object? sender, TerminalMessageEventArgs e)
    {
        if (e.Kind == TerminalMessageKind.ProcessExited)
            e.Text = $"\r\n[The program exited with code {e.ExitCode}.]\r\n";
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => Terminal.ClearScreen();

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = Terminal.CopyAsync();

    private void OnPasteClick(object? sender, RoutedEventArgs e) => _ = Terminal.PasteAsync();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        Terminal.Terminal.Selection.SelectAll();
        Terminal.Refresh();
    }
}
