using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>Clone a repository; closes with the cloned folder, or null (DataContext: <see cref="CloneRepositoryViewModel"/>).</summary>
public partial class CloneRepositoryWindow : Window
{
    public CloneRepositoryWindow()
    {
        InitializeComponent();
        Opened += (_, _) => UrlBox.Focus();
        DataContextChanged += OnDataContextChanged;
        // Closing mid-clone stops it (the half-made folder is removed).
        Closing += (_, _) => ViewModel?.CancelClone();
    }

    private CloneRepositoryViewModel? ViewModel => DataContext as CloneRepositoryViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is { } viewModel)
            viewModel.Done += folder => Close(folder);
    }

    /// <summary>Cancel: stop the clone if one is running, else close.</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.CancelClone() != true)
            Close(null);
    }
}
