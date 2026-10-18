using Avalonia.Controls;
using AXamlIDE.ViewModels.Settings;

namespace AXamlIDE.Views.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
