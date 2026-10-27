using Avalonia.Controls;
using Avalonia.Input;

namespace AXamlIDE.Views;

/// <summary>The start window (DataContext: <see cref="ViewModels.StartViewModel"/>). Alt+S goes to the recent search box, as in Visual Studio.</summary>
public partial class StartWindow : Window
{
    public StartWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Alt)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        };
    }
}
