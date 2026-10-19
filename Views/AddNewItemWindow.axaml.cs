using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>Add New Item; closes with true when the item can be added (DataContext: <see cref="AddNewItemViewModel"/>).</summary>
public partial class AddNewItemWindow : Window
{
    public AddNewItemWindow()
    {
        InitializeComponent();
        // Ready to type the name, with the part before the extension selected (as VS does).
        Opened += (_, _) =>
        {
            NameBox.Focus();
            var name = NameBox.Text ?? "";
            NameBox.SelectionStart = 0;
            NameBox.SelectionEnd = name.IndexOf('.') is var dot and > 0 ? dot : name.Length;
        };
    }

    private AddNewItemViewModel? ViewModel => DataContext as AddNewItemViewModel;

    private void Add()
    {
        if (ViewModel?.Accept() == true)
            Close(true);
    }

    private void OnAddClick(object? sender, RoutedEventArgs e) => Add();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnTemplateDoubleTapped(object? sender, TappedEventArgs e) => Add();

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Add();
    }
}
