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

    private void OnProblemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProblemItem problem } && DataContext is ProblemsViewModel viewModel)
            viewModel.OpenProblemCommand.Execute(problem);
    }
}
