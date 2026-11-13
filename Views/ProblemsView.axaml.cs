using Avalonia.Controls;
using Avalonia.Input;
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

    private void OnProblemDoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

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
