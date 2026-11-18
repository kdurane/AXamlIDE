using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AXamlIDE.ViewModels;
using Iciclecreek.Terminal;

namespace AXamlIDE.Views;

/// <summary>
/// The Terminal panel. DataContext: <see cref="TerminalViewModel"/>, which says when and where to start the shell;
/// the TerminalControl runs it. The colours follow the IDE's theme (<see cref="TerminalPalette"/>).
/// </summary>
public partial class TerminalView : UserControl
{
    private TerminalViewModel? _viewModel;
    private bool _templateApplied;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Terminal.InputSent += (_, _) => _viewModel?.OnInputSent();
        Terminal.ProcessExited += (_, _) => _viewModel?.OnShellExited();
        Terminal.WritingMessage += OnWritingMessage;
        // Tunnel: see the key before the terminal does, so a key after the shell has exited starts a new one.
        Terminal.AddHandler(KeyDownEvent, OnTerminalKeyDown, RoutingStrategies.Tunnel);
        // The control's terminal exists once its template is applied; colour it then and whenever the theme changes.
        Terminal.TemplateApplied += (_, _) =>
        {
            _templateApplied = true;
            TerminalPalette.Apply(Terminal);
            // The control stops its shell when it leaves the logical tree, which happens whenever Dock moves the panel
            // to another tab strip or a floating window. Keep it running; Restart and closing the app stop it (Stop).
            Terminal.BeginReparent();
        };
        Terminal.ActualThemeVariantChanged += (_, _) => ApplyPalette();

        // The menu shows this platform's copy and paste keys (Cmd+C on macOS).
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
            _viewModel.ClearRequested -= Terminal.ClearScreen;
            _viewModel.FocusRequested -= FocusTerminal;
            _viewModel.StopRequested -= Stop;
        }

        _viewModel = DataContext as TerminalViewModel;
        if (_viewModel is null)
            return;
        _viewModel.LaunchRequested += Launch;
        _viewModel.ClearRequested += Terminal.ClearScreen;
        _viewModel.FocusRequested += FocusTerminal;
        _viewModel.StopRequested += Stop;
        if (!Design.IsDesignMode)   // no shell in the previewer: it shows the panel as it is before one starts
            _viewModel.EnsureStarted();
        if (_viewModel.TakePendingFocusRequest())
            FocusTerminal();
    }

    /// <summary>Start the shell in <paramref name="folder"/>, stopping the one that's running.</summary>
    private void Launch(string folder)
    {
        if (_viewModel is null)
            return;
        Stop();
        Terminal.ClearScreen();
        var shell = _viewModel.Shell;
        Terminal.EnvironmentVariables ??= new System.Collections.Generic.Dictionary<string, string>();
        Terminal.EnvironmentVariables["TERM_PROGRAM"] = "AXamlIDE";   // VS Code sets "vscode", so scripts can tell
        _ = Terminal.LaunchProcess(folder, shell.Path, [.. shell.Arguments]);
    }

    private void ApplyPalette()
    {
        if (_templateApplied)
            TerminalPalette.Apply(Terminal);
    }

    private void Stop()
    {
        if (Terminal.IsLive)
            Terminal.Kill();
    }

    /// <summary>The shell has gone: say how to get a new one (VS Code's wording).</summary>
    private void OnWritingMessage(object? sender, TerminalMessageEventArgs e)
    {
        if (e.Kind == TerminalMessageKind.ProcessExited)
            e.Text = $"\r\n[The shell exited with code {e.ExitCode}. Press any key to start a new one.]\r\n";
    }

    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (Terminal.IsLive || _viewModel is null || IsModifier(e.Key))
            return;
        e.Handled = true;
        _viewModel.OnKeyAfterExit();
    }

    private static bool IsModifier(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;

    // After layout: the panel may have only just been switched to (or created).
    private void FocusTerminal() => Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Loaded);

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = Terminal.CopyAsync();

    private void OnPasteClick(object? sender, RoutedEventArgs e) => _ = Terminal.PasteAsync();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        Terminal.Terminal.Selection.SelectAll();
        Terminal.Refresh();
    }
}
