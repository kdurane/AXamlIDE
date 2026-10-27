using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>Create a new project; closes with the new solution's path, or null (DataContext: <see cref="NewProjectViewModel"/>).</summary>
public partial class NewProjectWindow : Window
{
    public NewProjectWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            SearchBox.Focus();
            if (ViewModel is { } viewModel)
                await viewModel.LoadAsync();
        };
        DataContextChanged += OnDataContextChanged;
        Closing += (_, _) => ViewModel?.CancelCreate();
    }

    private NewProjectViewModel? ViewModel => DataContext as NewProjectViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        viewModel.Done += solution => Close(solution);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>On to step 2: ready to type the project's name, all of it selected (as Visual Studio does).</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NewProjectViewModel.IsConfiguring) || ViewModel?.IsConfiguring != true)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            ProjectNameBox.Focus();
            ProjectNameBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnTemplateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel?.NextCommand.CanExecute(null) == true)
            ViewModel.NextCommand.Execute(null);
    }

    /// <summary>Cancel: stop a create that's running, else close.</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.CancelCreate() != true)
            Close(null);
    }
}
