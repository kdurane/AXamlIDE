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

        // Visual Studio's order: cut/copy/paste, then delete/rename.
        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var canChange = vm.CanChange(node);
        if (canChange)
        {
            menu.Items.Add(Item("Cut", vm.CutCommand, node, new KeyGesture(Key.X, KeyModifiers.Control)));
            menu.Items.Add(Item("Copy", vm.CopyCommand, node, new KeyGesture(Key.C, KeyModifiers.Control)));
        }
        menu.Items.Add(Item("Paste", vm.PasteCommand, node, new KeyGesture(Key.V, KeyModifiers.Control)));
        if (canChange)
        {
            menu.Items.Add(Item("Delete", vm.DeleteCommand, node, new KeyGesture(Key.Delete)));
            menu.Items.Add(Item("Rename", vm.RenameCommand, node, new KeyGesture(Key.F2)));
        }
        menu.Items.Add(new Separator());

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

        if (GitMenu(vm.Git, node) is { } git)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(git);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Properties", Command = vm.Properties.ShowCommand, InputGesture = new KeyGesture(Key.F4) });

        control.ContextMenu = menu;
        menu.Open(control);
        e.Handled = true;
    }

    private static MenuItem Item(string header, ICommand command, object parameter, KeyGesture? gesture = null)
        => new() { Header = header, Command = command, CommandParameter = parameter, InputGesture = gesture };

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

    /// <summary>
    /// The Git submenu (as in Rider) for a file, folder or project inside the repository: diff, stage/unstage,
    /// and add to / remove from .gitignore. A project acts on its folder. Null when nothing applies.
    /// </summary>
    private static MenuItem? GitMenu(GitViewModel git, ProjectTreeNode node)
    {
        var path = node.Kind switch
        {
            NodeKind.File or NodeKind.Directory or NodeKind.Folder => node.FullPath,
            NodeKind.Project => Path.GetDirectoryName(node.FullPath),
            _ => null
        };
        if (string.IsNullOrEmpty(path) || !git.Contains(path))
            return null;

        var menu = new MenuItem { Header = "Git" };
        if (node.Kind == NodeKind.File && git.ChangeFor(path) is not null)
            menu.Items.Add(Item("Show Diff", git.ShowDiffCommand, path));
        if (git.HasUnstagedChanges(path))
            menu.Items.Add(Item("Add", git.StagePathCommand, path));
        if (git.HasStagedChanges(path))
            menu.Items.Add(Item("Unstage", git.UnstagePathCommand, path));

        if (node.Kind is NodeKind.File or NodeKind.Directory or NodeKind.Folder)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            menu.Items.Add(node.GitState == GitFileState.Ignored
                ? Item("Remove from .gitignore", git.UnignoreCommand, path)
                : Item("Add to .gitignore", git.IgnoreCommand, path));
        }

        return menu.Items.Count > 0 ? menu : null;
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
