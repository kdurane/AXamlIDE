using System.Linq;
using Avalonia;
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

    private void OnStagedSelectionChanged(object? sender, SelectionChangedEventArgs e) => PassSelection(sender, stagedSide: true);

    private void OnChangesSelectionChanged(object? sender, SelectionChangedEventArgs e) => PassSelection(sender, stagedSide: false);

    private void PassSelection(object? sender, bool stagedSide)
    {
        if (sender is ListBox { SelectedItems: { } selected } && DataContext is GitViewModel viewModel)
            viewModel.SetSelection(stagedSide, selected.OfType<GitChangeItem>());
    }

    /// <summary>Clicking a row of the graph, as in VS Code: a commit opens or closes its files; a file opens its diff.</summary>
    private void OnGraphTapped(object? sender, TappedEventArgs e)
        => OpenGraphItem((e.Source as StyledElement)?.DataContext);

    private void OnGraphKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: { } item })
        {
            OpenGraphItem(item);
            e.Handled = true;
        }
    }

    private void OpenGraphItem(object? item)
    {
        if (DataContext is not GitViewModel viewModel)
            return;
        if (item is GitGraphCommitItem commit)
            viewModel.ToggleCommitFilesCommand.Execute(commit);
        else if (item is GitGraphFileItem file)
            viewModel.OpenCommitFileCommand.Execute(file);
    }
}
