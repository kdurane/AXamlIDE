using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

// ReSharper disable AsyncVoidEventHandlerMethod

namespace AXamlIDE.Views;

/// <summary>The Explorer panel (workspace tree). DataContext: <see cref="ExplorerViewModel"/>.</summary>
public partial class ExplorerView : UserControl
{
    // The dragged node travels inside the app as the object itself (not as text).
    private static readonly DataFormat<ProjectTreeNode> NodeFormat =
        DataFormat.CreateInProcessFormat<ProjectTreeNode>("AXamlIDE.ExplorerNode");

    private PointerPressedEventArgs? _dragStart;   // a left press on a node, until it becomes a drag or is let go
    private ProjectTreeNode? _dragNode;
    private TreeViewItem? _dropTarget;             // the row highlighted as the drop target

    public ExplorerView()
    {
        InitializeComponent();

        // TreeViewItem.Expanded bubbles, so one handler on the TreeView catches every node.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        FileTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        FileTree.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(FileTree, true);
        FileTree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FileTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropTarget(null));
        FileTree.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    // Children are loaded the first time a node is expanded.
    private async void OnNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: ProjectTreeNode { NeedsPopulating: true } node })
            await node.PopulateAsync();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        var node = item?.DataContext as ProjectTreeNode ?? FileTree.SelectedItem as ProjectTreeNode;
        if (node is not { Kind: NodeKind.File } || string.IsNullOrEmpty(node.FullPath))
            return;

        e.Handled = true;
        if (node.Children.Count > 0 && item is not null)
        {
            item.IsExpanded = true;
            node.IsExpanded = true;
        }

        ViewModel?.OpenFileCommand.Execute(node);
    }

    // Right-click selects the node under the pointer; the menu (ExplorerView.axaml) then opens for it.
    private ProjectTreeNode? _menuNode;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(FileTree).Properties.IsLeftButtonPressed)
        {
            _dragStart = e;
            _dragNode = ItemAt(e.Source)?.DataContext as ProjectTreeNode;
            return;
        }

        if (!e.GetCurrentPoint(FileTree).Properties.IsRightButtonPressed)
            return;

        var control = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        _menuNode = control?.DataContext as ProjectTreeNode;
        control?.IsSelected = true;
    }

    private static TreeViewItem? ItemAt(object? source)
    => (source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragNode is not { } node)
            return;
        var moved = e.GetPosition(FileTree) - start.GetPosition(FileTree);
        if (Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4)
            return;   // still a click
        _dragStart = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(NodeFormat, node));
        await DragDrop.DoDragDropAsync(start, transfer, DragDropEffects.Move);   // returns when it's dropped
        _dragNode = null;

    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // While dragging, Linux only says what kind of data it is (the data itself arrives on drop),
        // so check the kind, and use the node this view started dragging.
        var item = ItemAt(e.Source);
        var ok = e.DataTransfer.Contains(NodeFormat)
                 && _dragNode is { } dragged
                 && item?.DataContext is ProjectTreeNode target
                 && ViewModel?.CanDrop(dragged, target) == true;
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;   // the cursor shows which
        ShowDropTarget(ok ? item : null);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ShowDropTarget(null);
        if (e.DataTransfer.Contains(NodeFormat)
        && _dragNode is { } dragged
            && ItemAt(e.Source)?.DataContext is ProjectTreeNode target
            && ViewModel is { } vm)
            await vm.DropAsync(dragged, target);
    }

    private void ShowDropTarget(TreeViewItem? item)
    {
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = item;
        _dropTarget?.Classes.Add("dropTarget");
    }


    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || ViewModel is not { } vm || _menuNode is not { } node)
        {
            e.Cancel = true;   // not over a node
            return;
        }
        menu.DataContext = new ExplorerNodeMenu(node, vm);
        _menuNode = null;
    }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ExplorerNodeMenu { Node: var node } })
            await CopyPathAsync(node);
    }

    // F2, Delete and Ctrl+X/C/V act on the selected node while the tree has focus.
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || FileTree.SelectedItem is not ProjectTreeNode node)
            return;

        ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.F2, KeyModifiers.None) => vm.RenameCommand,
            (Key.Delete, KeyModifiers.None) => vm.DeleteCommand,
            (Key.X, KeyModifiers.Control) => vm.CutCommand,
            (Key.C, KeyModifiers.Control) => vm.CopyCommand,
            (Key.V, KeyModifiers.Control) => vm.PasteCommand,
            _ => null
        };
        if (command?.CanExecute(node) != true)
            return;
        command.Execute(node);
        e.Handled = true;
    }

    // Uses the clipboard of whichever window this panel is in.
    private async System.Threading.Tasks.Task CopyPathAsync(ProjectTreeNode node)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                            ?? throw new InvalidOperationException("The clipboard is unavailable.");
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(node.FullPath));
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ViewModel?.Status($"Couldn't copy path: {ex.Message}");
        }
    }
}
