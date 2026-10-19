using Avalonia.Controls;
using Avalonia.Input;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

public partial class CallStackView : UserControl
{
    public CallStackView()
    {
        InitializeComponent();
    }

    private CallStackViewModel? ViewModel => DataContext as CallStackViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is CallStackRow row)
            ViewModel?.ActivateCommand.Execute(row);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || List.SelectedItem is not CallStackRow row)
            return;
        ViewModel?.ActivateCommand.Execute(row);
        e.Handled = true;
    }
}
