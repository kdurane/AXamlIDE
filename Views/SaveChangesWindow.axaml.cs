using Avalonia.Controls;
using Avalonia.Interactivity;
using AXamlIDE.Models;

namespace AXamlIDE.Views;

/// <summary>The save prompt; closes with a <see cref="SaveChoice"/> (DataContext: ViewModels/SaveChangesViewModel).</summary>
public partial class SaveChangesWindow : Window
{
    public SaveChangesWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(SaveChoice.Save);

    private void OnDiscardClick(object? sender, RoutedEventArgs e) => Close(SaveChoice.Discard);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(SaveChoice.Cancel);
}
