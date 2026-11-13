using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Output panel (build output). DataContext: <see cref="RunViewModel"/>.</summary>
public partial class OutputView : UserControl
{
    public OutputView()
    {
        InitializeComponent();
    }

    // The line under the pointer (its TextBlock's text), to the view model, which opens the file it names.
    private void OnLineDoubleTapped(object? sender, TappedEventArgs e)
    {
        var block = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<SelectableTextBlock>().FirstOrDefault();
        if (block?.Text is { } line && DataContext is RunViewModel run)
            run.OpenOutputLineCommand.Execute(line);
    }
}
