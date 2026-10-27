using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Tests tool panel. DataContext: <see cref="TestExplorerViewModel"/>.</summary>
public partial class TestExplorerView : UserControl
{
    public TestExplorerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private TestExplorerViewModel? ViewModel => DataContext as TestExplorerViewModel;

    /// <summary>The panel is showing: list the tests the first time.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e) => ViewModel?.EnsureDiscovered();

    /// <summary>Double-click a test: open its code.</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemAt(e.Source)?.DataContext is TestNode { Test: not null } node)
            ViewModel?.GoToTestCommand.Execute(node);
    }

    /// <summary>Right-click selects the row under the pointer, so the menu acts on it.</summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ItemAt(e.Source)?.DataContext is TestNode node)
            TestTree.SelectedItem = node;
        else
            e.Handled = true;   // not over a row: no menu
    }

    private static TreeViewItem? ItemAt(object? source)
        => source as TreeViewItem ?? (source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>();
}
