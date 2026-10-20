using System.ComponentModel;
using Avalonia.Controls;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The view of one document tab (DataContext: <see cref="DocumentDockable"/>): its own editor and, for
/// AXAML files, a preview area laid out by the tab's <see cref="PreviewMode"/> and <see cref="PreviewSplit"/>.
///
/// There is one live preview (one designer host) and one outline in the app, and they follow the selected
/// tab. Each tab has its own preview view and outline drop-down, but only the selected tab's are connected
/// to the shared view models (DockViewCache calls <see cref="Connect"/> / <see cref="Disconnect"/>).
/// Controls are never moved between tabs, because a tab can be in another window and Avalonia doesn't
/// allow a control to change window while its layout is pending. Other AXAML tabs keep their layout and
/// show a placeholder. The bar above the editor only takes space when it has something to show.
/// </summary>
public partial class DocumentView : UserControl
{
    private readonly DocumentDockable _tab = null!;
    private OutlineView? _outline;
    private PreviewPaneView? _preview;

    /// <summary>For the XAML designer only.</summary>
    public DocumentView()
    {
        InitializeComponent();
    }

    public DocumentView(DocumentDockable tab, EditorServices services)
    {
        InitializeComponent();
        _tab = tab;
        DataContext = tab;

        Editor = new EditorPaneView();
        Editor.Initialize(services);
        Editor.SetDocument(tab.Document);
        EditorHost.Content = Editor;

        tab.PropertyChanged += OnTabPropertyChanged;
        ApplyLayout();
        UpdateBarVisibility();
    }

    public EditorPaneView Editor { get; } = null!;

    /// <summary>This tab is selected: connect its outline (and, for AXAML, its preview) to the shared view models.</summary>
    public void Connect(OutlineViewModel outline, PreviewPaneViewModel preview)
    {
        if (_outline is null)
        {
            _outline = new OutlineView();
            _outline.PropertyChanged += OnOutlinePropertyChanged;
            OutlineHost.Content = _outline;
        }
        _outline.DataContext = outline;
        OutlineHost.IsVisible = true;

        if (_tab.IsPreviewable)
        {
            _preview ??= new PreviewPaneView();
            _preview.DataContext = preview;
            PreviewHost.Content = _preview;
        }

        UpdateBarVisibility();
    }

    /// <summary>Another tab is selected: disconnect (so this tab's views stop following it) and show the placeholder.</summary>
    public void Disconnect()
    {
        OutlineHost.IsVisible = false;   // a disconnected drop-down would show empty
        _outline?.DataContext = null;
        _preview?.DataContext = null;
        PreviewHost.Content = PreviewPlaceholder;
        UpdateBarVisibility();
    }

    public void Shutdown()
    {
        _tab.PropertyChanged -= OnTabPropertyChanged;
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
        // SplitDirectionButton.Content = stacked ? "⇆" : "⇅";   // ⇆ / ⇅: what the click switches to
        SplitDirectionButtonIcon.Data = stacked ? IconResources.Get(this, "Icon.ArrowLeftRight") : IconResources.Get(this, "Icon.ArrowUpDown");
        ToolTip.SetTip(SplitDirectionButton, stacked ? "Show code and preview side by side" : "Show code and preview stacked");
        SwapSidesButton.IsEnabled = mode == PreviewMode.Split;
        ToolTip.SetTip(SwapSidesButton, stacked
            ? (_tab.PreviewFirst ? "Move the preview below the code" : "Move the preview above the code")
            : (_tab.PreviewFirst ? "Move the preview to the right" : "Move the preview to the left"));
    }
}
