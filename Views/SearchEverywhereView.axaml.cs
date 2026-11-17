using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The top bar's Search Everywhere box. DataContext: <see cref="SearchEverywhereViewModel"/>. The focus stays in the
/// box while the list is open (the arrow keys move the list's selection); choosing a row puts the focus back where
/// it was before (the editor), then runs the row, so an editor action acts on the editor.
/// </summary>
public partial class SearchEverywhereView : UserControl
{
    private SearchEverywhereViewModel? _viewModel;
    private IInputElement? _focusBefore;   // where the focus was before the box took it

    public SearchEverywhereView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel: before the TextBox uses Up/Down/Tab itself.
        Box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        // Clicked: remember the focus before the box takes it.
        Box.AddHandler(PointerPressedEvent, (_, _) => RememberFocus(), RoutingStrategies.Tunnel);
        Box.GotFocus += (_, _) => _viewModel?.Show();
        ResultList.Tapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchEverywhereItem item)
                Run(_viewModel?.Choose(item));
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusRequested -= FocusBox;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as SearchEverywhereViewModel;
        if (_viewModel is null)
            return;
        _viewModel.FocusRequested += FocusBox;
        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchEverywhereViewModel.SelectedIndex) && ResultList.SelectedItem is { } selected)
            ResultList.ScrollIntoView(selected);
    }

    private void RememberFocus()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is not null && !ReferenceEquals(focused, Box))
            _focusBefore = focused;
    }

    private void FocusBox()
    {
        RememberFocus();
        Box.Focus();
        Box.SelectAll();
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;
        switch (e.Key)
        {
            case Key.Down when vm.IsOpen:
                vm.MoveSelection(1);
                break;
            case Key.Up when vm.IsOpen:
                vm.MoveSelection(-1);
                break;
            case Key.Tab when vm.IsOpen:
                vm.NextTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.Enter:
                if (!vm.IsOpen)
                    vm.Show();
                else
                    Run(vm.Choose());
                break;
            case Key.Escape:
                vm.Close();
                RestoreFocus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The list has closed: focus back where it was, then do what the row does.</summary>
    private void Run(Action? action)
    {
        if (action is null)
            return;
        RestoreFocus();
        Dispatcher.UIThread.Post(action);
    }

    private void RestoreFocus()
    {
        if (_focusBefore is Visual visual && visual.IsAttachedToVisualTree() && _focusBefore.Focus())
        {
            _focusBefore = null;
            return;
        }
        _focusBefore = null;
        // Nothing was focused before (the app had just started): the editor on show, else stay in the box.
        TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<TextArea>().FirstOrDefault(area => area.IsEffectivelyVisible)?.Focus();
    }
}
