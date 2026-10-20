using System.ComponentModel;
using AXamlIDE.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One document tab in the dock layout, for one <see cref="OpenDocument"/>. Its view (Views/DocumentView:
/// the editor, and the preview for AXAML) is made by Views/DockViewCache and moves with the tab between
/// tab strips and windows. Each AXAML tab has its own preview mode and split.
/// </summary>
public sealed partial class DocumentDockable : Document
{
    public DocumentDockable(OpenDocument document, PreviewMode mode, PreviewSplit split, bool previewFirst)
    {
        Document = document;
        Id = document.Path;
        Title = document.DisplayTitle;
        CanPin = false;
        PreviewMode = mode;
        PreviewSplit = split;
        PreviewFirst = previewFirst;
        document.PropertyChanged += OnDocumentPropertyChanged;

        SetPreviewModeCommand = new RelayCommand<PreviewMode>(value => PreviewMode = value);
        TogglePreviewSplitCommand = new RelayCommand(() =>
            PreviewSplit = PreviewSplit == PreviewSplit.SideBySide ? PreviewSplit.Stacked : PreviewSplit.SideBySide);
        SwapPreviewSideCommand = new RelayCommand(() => PreviewFirst = !PreviewFirst);
    }

    public OpenDocument Document { get; }

    /// <summary>Only AXAML files have a preview (and the mode/split buttons).</summary>
    public bool IsPreviewable => PreviewPaneViewModel.IsPreviewable(Document);

    /// <summary>Code only, split, or design only. Ignored (always code) when the file isn't previewable.</summary>
    [ObservableProperty]
    public partial PreviewMode PreviewMode { get; set; }

    [ObservableProperty]
    public partial PreviewSplit PreviewSplit { get; set; }

    /// <summary>Preview before the code: on the left (side by side) or on top (stacked).</summary>
    [ObservableProperty]
    public partial bool PreviewFirst { get; set; }

    public RelayCommand<PreviewMode> SetPreviewModeCommand { get; }
    public RelayCommand SwapPreviewSideCommand { get; }
    public RelayCommand TogglePreviewSplitCommand { get; }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenDocument.DisplayTitle))
            Title = Document.DisplayTitle;
    }
}
