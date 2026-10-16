using Avalonia.Controls;
using AXaxmlIDE.ViewModels;

namespace AXaxmlIDE.Views;

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
