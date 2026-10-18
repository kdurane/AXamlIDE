using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AXamlIDE.ViewModels.Search;

namespace AXamlIDE.Views.Search;

/// <summary>The Search tool panel. DataContext: <see cref="SearchViewModel"/>.</summary>
public partial class SearchView : UserControl
{
    private SearchViewModel? _viewModel;

    public SearchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.FocusQueryRequested -= FocusQuery;
        _viewModel = DataContext as SearchViewModel;
        if (_viewModel is null)
            return;
        _viewModel.FocusQueryRequested += FocusQuery;
        if (_viewModel.TakePendingFocusRequest())
            FocusQuery();
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null) return;
        e.Handled = true;
        _viewModel.SearchWorkspaceCommand.Execute(null);
    }

    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null) return;
        e.Handled = true;
        _viewModel.ReplaceWorkspaceCommand.Execute(null);
    }

    // After layout: the panel may have only just been switched to (or created).
    private void FocusQuery() => Dispatcher.UIThread.Post(() =>
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }, DispatcherPriority.Loaded);
}
