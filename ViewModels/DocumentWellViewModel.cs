using System;
using System.Collections.ObjectModel;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using TextMateSharp.Grammars;
using DocumentCommands = AXamlIDE.Commands.DocumentCommands;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One document area: a tab strip, the outline drop-down, an editor and the AXAML preview.
/// There is one today (in MainWindow); floating windows will each get their own, with their own
/// <see cref="Documents"/> subset. The selected tab is always <see cref="DocumentManager.Active"/>.
/// </summary>
public sealed partial class DocumentWellViewModel : ObservableObject
{
    private readonly DocumentManager _documents;

    public DocumentWellViewModel(
        DocumentManager documents,
        DocumentCommands commands,
        OutlineViewModel outline,
        PreviewPaneViewModel preview,
        EditorServices editorServices,
        RegistryOptions registryOptions)
    {
        _documents = documents;
        Commands = commands;
        Outline = outline;
        Preview = preview;
        EditorServices = editorServices;
        RegistryOptions = registryOptions;

        _documents.ActiveChanged += _ =>
        {
            OnPropertyChanged(nameof(SelectedDocument));
            OnPropertyChanged(nameof(IsPreviewVisible));
        };
    }

    /// <summary>The tabs shown here (all open documents, until there is more than one well).</summary>
    public ObservableCollection<OpenDocument> Documents => _documents.Documents;

    /// <summary>The selected tab. Two-way with <see cref="DocumentManager.Active"/>.</summary>
    public OpenDocument? SelectedDocument
    {
        get => _documents.Active;
        set => _documents.Active = value;
    }

    /// <summary>The live preview is shown beside the editor for .axaml files.</summary>
    public bool IsPreviewVisible
        => SelectedDocument?.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true;

    public DocumentCommands Commands { get; }
    public OutlineViewModel Outline { get; }
    public PreviewPaneViewModel Preview { get; }

    // What the view's EditorPaneView is initialised with.
    public EditorServices EditorServices { get; }
    public RegistryOptions RegistryOptions { get; }
}
