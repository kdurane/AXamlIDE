using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;
using RunViewModel = AXamlIDE.ViewModels.RunViewModel;

namespace AXamlIDE.Running;

/// <summary>
/// Shows the floating debug toolbar while a debug session is running and closes it afterwards,
/// reopening it where the user last left it.
/// </summary>
public sealed class DebugToolbarController
{
    private readonly Window _owner;
    private readonly RunViewModel _run;
    private readonly Action<Window> _prepareWindow;
    private DebugToolbarWindow? _toolbar;
    private PixelPoint? _lastPosition;

    /// <param name="prepareWindow">Called for each new toolbar window before it opens (e.g. attach key bindings).</param>
    public DebugToolbarController(Window owner, RunViewModel run, Action<Window> prepareWindow)
    {
        _owner = owner;
        _run = run;
        _prepareWindow = prepareWindow;
        run.PropertyChanged += OnRunPropertyChanged;
    }

    private void OnRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RunViewModel.IsDebugging))
            return;

        if (_run.IsDebugging)
            Show();
        else
            Close();
    }

    private void Show()
    {
        if (_toolbar is not null)
            return;

        var toolbar = new DebugToolbarWindow(_lastPosition) { DataContext = _run };
        _prepareWindow(toolbar);
        toolbar.Closed += (_, _) =>
        {
            _lastPosition = toolbar.Position;   // reopen where the user left it
            if (ReferenceEquals(_toolbar, toolbar))
                _toolbar = null;
        };
        _toolbar = toolbar;
        toolbar.ShowOver(_owner);   // owned: minimises with the main window and closes with it
    }

    private void Close()
    {
        _toolbar?.Close();
        _toolbar = null;
    }
}
