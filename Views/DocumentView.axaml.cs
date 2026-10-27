using System.ComponentModel;
using Avalonia.Controls;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The view of one document tab (DataContext: <see cref="DocumentDockable"/>): its own editor and, for
/// AXAML and Markdown files, a preview area laid out by the tab's <see cref="PreviewMode"/> and <see cref="PreviewSplit"/>.
///
/// Each AXAML tab has its own preview (its own view model and designer host, from PreviewSessions), so it keeps
/// showing whichever tab is selected, even dragged out to another window. There is one outline in the app,
/// and it follows the selected tab: only the selected tab's drop-down is connected to it (DockViewCache calls
/// <see cref="Connect"/> / <see cref="Disconnect"/>). Controls are never moved between tabs, because a tab can
/// be in another window and Avalonia doesn't allow a control to change window while its layout is pending.
/// The bar above the editor only takes space when it has something to show.
/// </summary>
public partial class DocumentView : UserControl
{
    private readonly DocumentDockable _tab = null!;
    private OutlineView? _outline;

    /// <summary>For the XAML designer only.</summary>
    public DocumentView()
    {
        InitializeComponent();
    }

    /// <param name="preview">This tab's preview (the AXAML designer, or the rendered Markdown); null when it has none.</param>
    public DocumentView(DocumentDockable tab, EditorServices services, Control? preview)
    {
        InitializeComponent();
        _tab = tab;
        DataContext = tab;

        Editor = new EditorPaneView();
        Editor.Initialize(services);
        Editor.SetDocument(tab.Document);
        EditorHost.Content = Editor;

        PreviewHost.Content = preview;

        tab.PropertyChanged += OnTabPropertyChanged;
        ApplyLayout();
        UpdateBarVisibility();
    }

    public EditorPaneView Editor { get; } = null!;

    /// <summary>This tab is selected: connect its outline drop-down to the shared outline.</summary>
    public void Connect(OutlineViewModel outline)
    {
        if (_outline is null)
        {
            _outline = new OutlineView();
            _outline.PropertyChanged += OnOutlinePropertyChanged;
            OutlineHost.Content = _outline;
        }
        _outline.DataContext = outline;
        OutlineHost.IsVisible = true;
        UpdateBarVisibility();
    }

    /// <summary>Another tab is selected: disconnect the outline drop-down (so it stops following the selected tab).</summary>
    public void Disconnect()
    {
        OutlineHost.IsVisible = false;   // a disconnected drop-down would show empty
        _outline?.DataContext = null;
        UpdateBarVisibility();
    }

    public void Shutdown()
    {
        _tab.PropertyChanged -= OnTabPropertyChanged;
        (PreviewHost.Content as MarkdownPreviewView)?.Shutdown();
        Editor.Shutdown();
    }

    private void OnOutlinePropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
            UpdateBarVisibility();
    }

    // The outline view is visible only while it has items (C# files), so the bar shows for those and for AXAML.
    private void UpdateBarVisibility()
        => DocumentBar.IsVisible = _tab.IsPreviewable || _outline is { DataContext: not null, IsVisible: true };

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentDockable.PreviewMode) or nameof(DocumentDockable.PreviewSplit)
            or nameof(DocumentDockable.PreviewFirst))
            ApplyLayout();
    }

    /// <summary>
    /// Editor | splitter | preview (or preview first), as columns (side by side) or rows (stacked);
    /// unused parts get no space.
    /// </summary>
    private void ApplyLayout()
    {
        var mode = _tab.IsPreviewable ? _tab.PreviewMode : PreviewMode.Code;
        var stacked = _tab.PreviewSplit == PreviewSplit.Stacked;
        var (editor, splitter, preview) = mode switch
        {
            PreviewMode.Split => (GridLength.Star, new GridLength(4), GridLength.Star),
            PreviewMode.Design => (new GridLength(0), new GridLength(0), GridLength.Star),
            _ => (GridLength.Star, new GridLength(0), new GridLength(0))
        };

        Control[] parts = _tab.PreviewFirst ? [PreviewHost, Splitter, EditorHost] : [EditorHost, Splitter, PreviewHost];
        GridLength[] sizes = _tab.PreviewFirst ? [preview, splitter, editor] : [editor, splitter, preview];

        Surface.RowDefinitions.Clear();
        Surface.ColumnDefinitions.Clear();
        for (var index = 0; index < 3; index++)
        {
            if (stacked)
                Surface.RowDefinitions.Add(new RowDefinition(sizes[index]));
            else
                Surface.ColumnDefinitions.Add(new ColumnDefinition(sizes[index]));
            Grid.SetRow(parts[index], stacked ? index : 0);
            Grid.SetColumn(parts[index], stacked ? 0 : index);
        }

        EditorHost.IsVisible = mode != PreviewMode.Design;
        Splitter.IsVisible = mode == PreviewMode.Split;
        PreviewHost.IsVisible = mode != PreviewMode.Code;
        Splitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;

        CodeButton.IsChecked = mode == PreviewMode.Code;
        SplitButton.IsChecked = mode == PreviewMode.Split;
        DesignButton.IsChecked = mode == PreviewMode.Design;
        SplitDirectionButton.Classes.Set("stacked", stacked);
        SplitDirectionButton.Classes.Set("nonstacked", !stacked);
        ToolTip.SetTip(SplitDirectionButton, stacked ? "Show code and preview side by side" : "Show code and preview stacked");
        SwapSidesButton.IsEnabled = mode == PreviewMode.Split;
        ToolTip.SetTip(SwapSidesButton, stacked
            ? (_tab.PreviewFirst ? "Move the preview below the code" : "Move the preview above the code")
            : (_tab.PreviewFirst ? "Move the preview to the right" : "Move the preview to the left"));
    }
}
