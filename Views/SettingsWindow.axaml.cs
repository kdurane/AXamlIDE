using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <param name="page">The tab to open on (its header), e.g. "IntelliSense"; null for the first.</param>
    /// <param name="option">A check box's text on that page to scroll to and focus (Search Everywhere's options).</param>
    public SettingsWindow(SettingsViewModel viewModel, string? page = null, string? option = null) : this()
    {
        DataContext = viewModel;
        if (Pages.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Header as string == page) is { } tab)
            Pages.SelectedItem = tab;
        if (option is not null)
            Opened += (_, _) => Dispatcher.UIThread.Post(() => FocusOption(option), DispatcherPriority.Loaded);
    }

    private void FocusOption(string option)
    {
        if (this.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(box => box.Content as string == option) is not { } checkBox)
            return;
        checkBox.BringIntoView();
        checkBox.Focus(Avalonia.Input.NavigationMethod.Tab);   // Tab: shows the focus ring, so it stands out
    }
}
