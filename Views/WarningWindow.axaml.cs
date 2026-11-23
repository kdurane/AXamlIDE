using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AXamlIDE.Views;

/// <summary>The warning before a change that can't be undone; closes with the choice clicked, or null (DataContext: ViewModels/WarningViewModel).</summary>
public partial class WarningWindow : Window
{
    public WarningWindow()
    {
        InitializeComponent();
    }

    private void OnChoiceClick(object? sender, RoutedEventArgs e) => Close((sender as Button)?.DataContext as string);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
