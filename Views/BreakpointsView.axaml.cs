using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

public partial class BreakpointsView : UserControl
{
    public BreakpointsView()
    {
        InitializeComponent();
    }

    /// <summary>Double-click a row (not its check box) to go to the breakpoint.</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<CheckBox>(includeSelf: true) is null
            && source.DataContext is BreakpointRow row)
            (DataContext as BreakpointsViewModel)?.GoToCommand.Execute(row);
    }
}
