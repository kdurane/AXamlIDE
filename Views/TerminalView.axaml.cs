using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Terminal tool panel. DataContext: <see cref="TerminalViewModel"/>.</summary>
public partial class TerminalView : UserControl
{
    private TerminalViewModel? _viewModel;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        OutputEditor.Options.AllowScrollBelowDocument = false;   // the newest line at the bottom, as in a terminal
        // Tunnel: before the text box moves its caret for Up/Down.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        // A click in the output that doesn't select anything goes to the command line, as in a terminal.
        OutputEditor.TextArea.AddHandler(PointerReleasedEvent, OnOutputClicked, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.OutputAppended -= OnOutputAppended;
            _viewModel.FocusRequested -= FocusInput;
        }

        _viewModel = DataContext as TerminalViewModel;
        if (_viewModel is null)
            return;
        _viewModel.OutputAppended += OnOutputAppended;
        _viewModel.FocusRequested += FocusInput;
        _viewModel.EnsureStarted();
        if (_viewModel.TakePendingFocusRequest())
            FocusInput();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                _viewModel.SubmitCommand.Execute(null);
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                _viewModel.HistoryPreviousCommand.Execute(null);
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                _viewModel.HistoryNextCommand.Execute(null);
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                break;
            case Key.C when ctrl && InputBox.SelectionStart == InputBox.SelectionEnd:
                _viewModel.InterruptCommand.Execute(null);   // with a selection, Ctrl+C copies as usual
                break;
            case Key.L when ctrl:
                _viewModel.ClearCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Keep showing the newest output, unless the user has scrolled up to read something.</summary>
    private void OnOutputAppended()
    {
        // The extent here is from before the new text was laid out, so "at the end" allows a little slack.
        var atEnd = OutputEditor.VerticalOffset + OutputEditor.ViewportHeight >= OutputEditor.ExtentHeight - 40;
        if (atEnd)
            Dispatcher.UIThread.Post(OutputEditor.ScrollToEnd, DispatcherPriority.Loaded);
    }

    private void OnOutputClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (OutputEditor.TextArea.Selection.IsEmpty)
            InputBox.Focus();
    }

    // After layout: the panel may have only just been switched to (or created).
    private void FocusInput() => Dispatcher.UIThread.Post(() => InputBox.Focus(), DispatcherPriority.Loaded);
}
