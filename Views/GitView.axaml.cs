using Avalonia.Controls;
using Avalonia.Input;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Git tab (in the explorer's dock). DataContext: <see cref="GitViewModel"/>.</summary>
public partial class GitView : UserControl
{
    public GitView()
    {
        InitializeComponent();
    }

    private void OnChangeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: GitChangeItem item } && DataContext is GitViewModel viewModel)
            viewModel.OpenDiffCommand.Execute(item);
    }
}
