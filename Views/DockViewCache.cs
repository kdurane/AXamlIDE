using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Recycling.Model;
using Avalonia.VisualTree;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.Shell;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// Makes and keeps the view of every dock tab: a <see cref="DocumentView"/> (editor + preview area) per
/// document, and the panel view per tool. It plugs into Dock as its control cache (set on every
/// DockControl in App.axaml.cs), which Dock asks only for tab content. That matters: the views are
/// deliberately NOT app-wide data templates, or Dock's tab headers and drag preview would try to show
/// the same editor too ("already has a visual parent").
///
/// A view is made once and moved, so a tab's editor (caret, undo, folding) survives being dragged to
/// another tab strip or window. It also connects the selected tab's view to the live preview and outline.
/// Everything else Dock asks for (its own dock controls) goes to Dock's normal cache.
/// </summary>
public sealed class DockViewCache : IControlRecycling
{
    private readonly EditorServices _editorServices;
    private readonly ToolPanels _toolPanels;
    private readonly DocumentManager _documents;
    private readonly PreviewPaneViewModel _previewViewModel;
    private readonly OutlineViewModel _outlineViewModel;
    private readonly ControlRecycling _dockControls = new();
    private readonly Dictionary<DocumentDockable, DocumentView> _documentViews = [];
    private DocumentView? _connected;   // the selected tab's view, connected to the outline and preview

    public DockViewCache(
        EditorServices editorServices,
        ToolPanels toolPanels,
        DockLayoutViewModel docking,
        DocumentManager documents,
        PreviewPaneViewModel preview,
        OutlineViewModel outline)
    {
        _editorServices = editorServices;
        _toolPanels = toolPanels;
        _documents = documents;
        _previewViewModel = preview;
        _outlineViewModel = outline;
        docking.DocumentTabRemoved += OnDocumentTabRemoved;
        documents.ActiveChanged += _ => PlaceSharedViews();
    }

    // ---------------------------------------------------------------- IControlRecycling

    public bool TryToUseIdAsKey
    {
        get => _dockControls.TryToUseIdAsKey;
        set => _dockControls.TryToUseIdAsKey = value;
    }

    public object? Build(object? data, object? existing, object? parent)
    {
        Control? view = data switch
        {
            DocumentDockable tab => DocumentViewFor(tab),
            ToolDockable tool => _toolPanels.ViewFor(tool.Id),
            _ => null
        };
        if (view is null)
            return _dockControls.Build(data, existing, parent);

        if (ReferenceEquals(view, existing))
            return view;
        return Detach(view) ? view : null;
    }

    public bool TryGetValue(object? data, out object? control)
    {
        if (data is DocumentDockable tab && _documentViews.TryGetValue(tab, out var view))
        {
            control = view;
            return true;
        }

        return _dockControls.TryGetValue(data, out control);
    }

    public void Add(object data, object control)
    {
        if (data is not (DocumentDockable or ToolDockable))
            _dockControls.Add(data, control);
    }

    public void Clear() => _dockControls.Clear();

    // ---------------------------------------------------------------- documents

    private DocumentView DocumentViewFor(DocumentDockable tab)
    {
        if (_documentViews.TryGetValue(tab, out var view))
            return view;

        view = new DocumentView(tab, _editorServices);
        _documentViews[tab] = view;
        PlaceSharedViews();
        return view;
    }

    private void OnDocumentTabRemoved(DocumentDockable tab)
    {
        if (!_documentViews.Remove(tab, out var view))
            return;
        if (ReferenceEquals(view, _connected))
        {
            view.Disconnect();
            _connected = null;
        }

        Detach(view);
        view.Shutdown();
    }

    /// <summary>Connect the selected tab's view to the outline and live preview, and disconnect the last one.</summary>
    private void PlaceSharedViews()
    {
        DocumentView? selected = null;
        foreach (var (tab, view) in _documentViews)
            if (ReferenceEquals(tab.Document, _documents.Active))
                selected = view;

        if (ReferenceEquals(selected, _connected))
            return;

        _connected?.Disconnect();
        _connected = selected;
        selected?.Connect(_outlineViewModel, _previewViewModel);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Take a view out of the presenter or panel showing it, so it can be shown somewhere else.</summary>
    private static bool Detach(Control view)
    {
        switch (view.GetVisualParent() ?? view.Parent)
        {
            case null:
                return true;
            case ContentPresenter presenter when ReferenceEquals(presenter.Child, view):
                presenter.SetCurrentValue(ContentPresenter.ContentProperty, null);
                presenter.UpdateChild();
                break;
            case Panel panel:
                panel.Children.Remove(view);
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, view):
                contentControl.SetCurrentValue(ContentControl.ContentProperty, null);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, view):
                decorator.Child = null;
                break;
        }

        return view.GetVisualParent() is null;
    }
}
