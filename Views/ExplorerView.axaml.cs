using System;
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

namespace AXamlIDE.Views.Workspace;

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

    // Right-click: select the node and show what can be done to it.
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FileTree).Properties.IsRightButtonPressed || ViewModel is not { } vm)
            return;

        var control = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (control is not { DataContext: ProjectTreeNode node })
            return;

        control.IsSelected = true;
        var menu = new ContextMenu();
        if (node.Kind == NodeKind.File)
            menu.Items.Add(Item("Open", vm.OpenFileCommand, node));
        if (!string.IsNullOrEmpty(node.FullPath))
        {
            var copy = new MenuItem { Header = "Copy Path" };
            copy.Click += async (_, _) => await CopyPathAsync(node);
            menu.Items.Add(copy);
        }
        if (node.Kind is NodeKind.Directory or NodeKind.Solution or NodeKind.Project or NodeKind.Folder)
            menu.Items.Add(Item("Refresh", vm.RefreshCommand, node));
        if (node.Kind == NodeKind.Project
            || node.Kind == NodeKind.File && node.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Build", vm.Run.BuildProjectCommand, node));
            menu.Items.Add(Item("Run", vm.Run.RunProjectCommand, node));
            menu.Items.Add(Item("Start Debugging", vm.Run.StartDebuggingCommand, node));
        }

        if (menu.Items.Count == 0)
            return;

        control.ContextMenu = menu;
        menu.Open(control);
        e.Handled = true;
    }

    private static MenuItem Item(string header, ICommand command, ProjectTreeNode node)
        => new() { Header = header, Command = command, CommandParameter = node };

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
