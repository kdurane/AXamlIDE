using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The debugger value view's input: resizing columns, double-click (expand, or edit a value), keys (Right / Left
/// expand and collapse, Enter toggles, F2 edits, Ctrl+C copies the value, Delete removes a watch) and the
/// right-click menu. The rows and their actions are the DebugValueTree's.
/// </summary>
public partial class DebugValueTreeView : UserControl
{
    public static readonly StyledProperty<double> NameColumnWidthProperty =
        AvaloniaProperty.Register<DebugValueTreeView, double>(nameof(NameColumnWidth), 200);

    public static readonly StyledProperty<double> TypeColumnWidthProperty =
        AvaloniaProperty.Register<DebugValueTreeView, double>(nameof(TypeColumnWidth), 220);

    public static readonly StyledProperty<bool> ShowHeaderProperty =
        AvaloniaProperty.Register<DebugValueTreeView, bool>(nameof(ShowHeader), true);

    public static readonly StyledProperty<bool> ShowTypeColumnProperty =
        AvaloniaProperty.Register<DebugValueTreeView, bool>(nameof(ShowTypeColumn), true);

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<DebugValueTreeView, bool>(nameof(IsEditable), true);

    /// <summary>Delete key / "Delete Watch" on a root row (the Watch window binds its Remove command).</summary>
    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<DebugValueTreeView, ICommand?>(nameof(DeleteCommand));

    public DebugValueTreeView()
    {
        InitializeComponent();
    }

    public double NameColumnWidth { get => GetValue(NameColumnWidthProperty); set => SetValue(NameColumnWidthProperty, value); }
    public double TypeColumnWidth { get => GetValue(TypeColumnWidthProperty); set => SetValue(TypeColumnWidthProperty, value); }
    public bool ShowHeader { get => GetValue(ShowHeaderProperty); set => SetValue(ShowHeaderProperty, value); }
    public bool ShowTypeColumn { get => GetValue(ShowTypeColumnProperty); set => SetValue(ShowTypeColumnProperty, value); }
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }

    private DebugValueNode? Selected => List.SelectedItem as DebugValueNode;

    private void OnNameSplitterDrag(object? sender, VectorEventArgs e)
        => NameColumnWidth = System.Math.Max(60, NameColumnWidth + e.Vector.X);

    private void OnTypeSplitterDrag(object? sender, VectorEventArgs e)
        => TypeColumnWidth = System.Math.Max(40, TypeColumnWidth - e.Vector.X);

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not DebugValueNode node)
            return;
        if (IsEditable && node.CanEdit)
            BeginEdit(node);
        else if (node.HasChildren)
            node.ToggleCommand.Execute(null);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Selected is not { } node || node.IsEditing)
            return;
        switch (e.Key)
        {
            case Key.Right when node.HasChildren && !node.IsExpanded:
            case Key.Left when node.IsExpanded:
            case Key.Enter when node.HasChildren:
                node.ToggleCommand.Execute(null);
                break;
            case Key.F2 when IsEditable && node.CanEdit:
                BeginEdit(node);
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                node.CopyValueCommand.Execute(null);
                break;
            case Key.Delete when DeleteCommand?.CanExecute(node) == true:
                DeleteCommand.Execute(node);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Start editing and put the cursor in the box, all of the value selected.</summary>
    private void BeginEdit(DebugValueNode node)
    {
        node.BeginEditCommand.Execute(null);
        Dispatcher.UIThread.Post(() =>
        {
            if (List.ContainerFromItem(node) is { } row && row.FindDescendantOfType<TextBox>() is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DebugValueNode { IsEditing: true } node)
            node.CommitEditCommand.Execute(null);
    }

    // ---------------------------------------------------------------- right-click menu

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Selected is not { } node)
        {
            e.Cancel = true;
            return;
        }
        EditItem.IsVisible = IsEditable && node.CanEdit;
        AddWatchItem.IsVisible = node.Expression is not null;
        VisualizeItem.IsVisible = node.CanVisualize;
        DeleteItem.IsVisible = DeleteCommand?.CanExecute(node) == true;
    }

    private void OnCopyValue(object? sender, RoutedEventArgs e) => Selected?.CopyValueCommand.Execute(null);
    private void OnCopyExpression(object? sender, RoutedEventArgs e) => Selected?.CopyExpressionCommand.Execute(null);
    private void OnAddWatch(object? sender, RoutedEventArgs e) => Selected?.AddWatchCommand.Execute(null);
    private void OnVisualize(object? sender, RoutedEventArgs e) => Selected?.VisualizeCommand.Execute(null);

    private void OnEditValue(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } node)
            BeginEdit(node);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } node && DeleteCommand?.CanExecute(node) == true)
            DeleteCommand.Execute(node);
    }
}
