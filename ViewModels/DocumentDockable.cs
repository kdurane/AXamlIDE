using System;
﻿using System.ComponentModel;
using AXamlIDE.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One document tab in the dock layout, for one <see cref="OpenDocument"/>. Its view (Views/DocumentView:
/// the editor, and the preview for AXAML) is made by Views/DockViewCache and moves with the tab between
/// tab strips and windows. Each AXAML tab has its own preview mode and split
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
        ReloadFromDiskCommand = new RelayCommand(() => Document.ReloadFromDisk());
        KeepMineCommand = new RelayCommand(Document.KeepOwnChanges);
    }

    public OpenDocument Document { get; }

    /// <summary>AXAML files (the designer) and Markdown files have a preview, and the mode/split buttons.</summary>
    public bool IsPreviewable => IsXaml || IsMarkdown;

    /// <summary>An AXAML file: its preview is the designer (PreviewPaneView).</summary>
    public bool IsXaml => PreviewPaneViewModel.IsPreviewable(Document);

    /// <summary>A Markdown file: its preview is the rendered page (MarkdownPreviewView).</summary>
    public bool IsMarkdown => Document.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                              || Document.Path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>The preview-only button's name: the designer for AXAML, the rendered page for Markdown.</summary>
    public string PreviewOnlyLabel => IsMarkdown ? "Preview" : "Design";

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

    /// <summary>The "changed on disk" bar: take the disk's text (Ctrl+Z gets the edits back), or keep the edits.</summary>
    public RelayCommand ReloadFromDiskCommand { get; }
    public RelayCommand KeepMineCommand { get; }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenDocument.DisplayTitle))
            Title = Document.DisplayTitle;
    }
}
