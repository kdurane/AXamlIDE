using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The Search Everywhere window. DataContext: <see cref="SearchEverywhereViewModel"/>. One window, hidden rather than
/// closed, so it opens again where it was dragged to. It closes on Escape, when a row is chosen, or when the IDE is
/// clicked (it loses activation), as Rider's does. A chosen row runs after the main window has the focus back.
/// </summary>
public partial class SearchEverywhereWindow : Window
{
    private readonly Window _owner;
    private readonly SearchEverywhereViewModel _viewModel;
    private bool _placed;       // shown once: later shows keep the place it was dragged to
    private bool _hiding;

    public SearchEverywhereWindow()
    {
        InitializeComponent();
        _owner = this;
        _viewModel = null!;
    }

    public SearchEverywhereWindow(Window owner, SearchEverywhereViewModel viewModel) : this()
    {
        _owner = owner;
        _viewModel = viewModel;
        DataContext = viewModel;

        // Tunnel: before the TextBox uses Up/Down/Tab itself.
        Box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        ResultList.Tapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<ToggleSwitch>(includeSelf: true) is not null)
                return;   // the option's switch: it changes the option, the row isn't chosen
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchEverywhereItem item)
                Run(_viewModel.Choose(item));
        };
        viewModel.PropertyChanged += OnViewModelChanged;

        // A click back in the IDE closes it (not while it's being dragged or resized: it stays active then).
        Deactivated += (_, _) => HideAndClear();
    }

    /// <summary>Show it over the owner (centred near the top the first time), with the keyboard in the box.</summary>
    public void ShowOver()
    {
        if (!IsVisible)
        {
            if (!_placed)
            {
                var scale = _owner.RenderScaling;
                var x = _owner.Position.X + (int)((_owner.Bounds.Width - Width) / 2 * scale);
                var y = _owner.Position.Y + (int)(_owner.Bounds.Height / 7 * scale);
                Position = new PixelPoint(x, y);
                _placed = true;
            }
            Show(_owner);
        }
        Activate();
        Dispatcher.UIThread.Post(() =>
        {
            Box.Focus();
            Box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchEverywhereViewModel.SelectedIndex) && ResultList.SelectedItem is { } selected)
            ResultList.ScrollIntoView(selected);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                break;
            case Key.Tab:
                _viewModel.NextTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.Enter:
                Run(_viewModel.Choose());
                break;
            case Key.Escape:
                HideAndClear();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Close, give the main window the focus back, then do what the row does.</summary>
    private void Run(Action? action)
    {
        if (action is null)
            return;
        HideAndClear();
        Dispatcher.UIThread.Post(action, DispatcherPriority.Input);
    }

    private void HideAndClear()
    {
        if (_hiding || !IsVisible)
            return;
        _hiding = true;
        try
        {
            Hide();
            _viewModel.Close();
            _owner.Activate();   // the focus goes back to what had it there (the editor)
        }
        finally
        {
            _hiding = false;
        }
    }
}
