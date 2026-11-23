using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Problems tool panel. DataContext: <see cref="ProblemsViewModel"/>.</summary>
public partial class ProblemsView : UserControl
{
    public ProblemsView()
    {
        InitializeComponent();
    }

    /// <summary>A double-click anywhere on a row opens it (not on the empty space below the rows).</summary>
    private void OnProblemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            OpenSelected();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (List.SelectedItem is ProblemItem problem && DataContext is ProblemsViewModel viewModel)
            viewModel.OpenProblemCommand.Execute(problem);
    }
}
