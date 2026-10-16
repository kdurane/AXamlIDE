using Avalonia.Controls;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views
{
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
}
