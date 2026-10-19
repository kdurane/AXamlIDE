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
    public ExplorerView()
    {
        InitializeComponent();

        // TreeViewItem.Expanded bubbles, so one handler on the TreeView catches every node.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        FileTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Bubble);
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
        if (!e.GetCurrentPoint(FileTree).Properties.IsRightButtonPressed)
            return;

        var control = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        _menuNode = control?.DataContext as ProjectTreeNode;
        if (control is not null)
            control.IsSelected = true;
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
        if ((sender as MenuItem)?.DataContext is ExplorerNodeMenu { Node: var node })
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
