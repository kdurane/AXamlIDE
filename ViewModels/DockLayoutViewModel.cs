using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using AXamlIDE.Commands;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The dock layout (document tabs and tool panels, in the main window and any floating windows),
/// kept in step with the rest of the app:
///
///   - one document tab per <see cref="DocumentManager.Documents"/> entry, added to the tab strip the
///     user is working in (the one holding the selected tab);
///   - <see cref="DocumentManager.Active"/> and the tab the user is in follow each other, in any window;
///   - closing a tab goes through <see cref="DocumentCommands"/> (so unsaved changes are asked about);
///   - closing a floating window moves its tabs back into the main window rather than losing them;
///   - <see cref="ShowToolTabMessage"/> brings a tool panel to the front (putting it back if needed);
///     Search opens in its own window, which remembers where it was;
///   - the layout's sizes can be captured and re-applied (WorkspaceSession keeps them per workspace).
///
/// Nothing else talks to the dock model; the views (DockControl, DockViewCache) only show it.
/// </summary>
public sealed class DockLayoutViewModel : IRecipient<ShowToolTabMessage>
{
    private readonly DockFactory _factory;
    private readonly DocumentManager _documents;
    private readonly DocumentCommands _commands;
    private readonly SettingsViewModel _settings;
    private readonly Dictionary<OpenDocument, DocumentDockable> _tabs = [];
    private readonly Dictionary<string, PageDockable> _pages = [];
    private IDock? _lastActiveDock;
    private PreviewMode _previewMode = PreviewMode.Split;      // new AXAML tabs start like the last one changed
    private PreviewSplit _previewSplit = PreviewSplit.SideBySide;
    private bool _previewFirst;
    private bool _syncing;
    private bool _shuttingDown;
    private (double X, double Y, double Width, double Height)? _searchWindowBounds;

    public DockLayoutViewModel(
        DocumentManager documents,
        DocumentCommands commands,
        SettingsViewModel settings,
        WorkspaceViewModel workspace,
        IMessenger messenger)
    {
        _documents = documents;
        _commands = commands;
        _settings = settings;

        _factory = new DockFactory(new WelcomeViewModel(workspace));
        Root = _factory.CreateLayout(settings.SolutionExplorerOnRight);
        _factory.InitLayout(Root);

        _documents.Documents.CollectionChanged += OnDocumentsChanged;
        _documents.ActiveChanged += OnActiveDocumentChanged;
        _factory.GlobalDockTrackingChanged += (_, e) => OnDockFocused(e.Current.Dockable);
        _factory.FocusedDockableChanged += (_, e) => OnDockFocused(e.Dockable);
        _factory.ActiveDockableChanged += OnDockActiveDockableChanged;
        _factory.DockableClosing += OnDockableClosing;
        _factory.DockableClosed += OnDockableClosed;
        _factory.WindowClosing += OnWindowClosing;
        settings.PropertyChanged += OnSettingsChanged;
        messenger.RegisterAll(this);
    }

    /// <summary>The layout the main window's DockControl shows.</summary>
    public IRootDock Root { get; }

    /// <summary>The dock factory, for the views (host windows for floating tabs).</summary>
    public IFactory Factory => _factory;

    /// <summary>
    /// The main window's screen bounds, kept current by MainWindow. Used to place a floating tool window
    /// (Search) the first time it opens.
    /// </summary>
    public (double X, double Y, double Width, double Height) MainWindowBounds { get; set; } = (100, 100, 1200, 800);

    /// <summary>The tab of an open document, or null.</summary>
    public DocumentDockable? TabFor(OpenDocument document) => _tabs.GetValueOrDefault(document);

    /// <summary>Raised after a document or diff tab has left the layout for good (its view can be released).</summary>
    public event Action<IDockable>? TabRemoved;

    // ------------------------------------------------------------- tools

    public void Receive(ShowToolTabMessage message) => ShowTool(message.PanelId);

    /// <summary>Bring a tool panel to the front of its tab strip, wherever it is.</summary>
    public void ShowTool(string id)
    {
        if (!_factory.Tools.TryGetValue(id, out var tool))
            return;

        if (_factory.FloatsByDefault(tool) && !_factory.IsInLayout(tool))
        {
            var main = MainWindowBounds;
            var (x, y, width, height) = _searchWindowBounds
                ?? (main.X + main.Width / 2 - 350, main.Y + 80, 700, Math.Min(560, main.Height - 120));
            _factory.FloatTool(tool, x, y, width, height);
        }
        else
        {
            _factory.PutBack(tool);
        }

        _factory.SetActiveDockable(tool);
        if (_factory.FindRoot(tool, _ => true)?.Window is { } window)
            window.SetActive();   // bring its floating window to the front
    }

    /// <summary>Whether a tool panel is showing (in a window that's open).</summary>
    public bool IsToolShown(string id) => _factory.Tools.TryGetValue(id, out var tool) && _factory.IsInLayout(tool);

    /// <summary>
    /// The activity bar's explorer button: hide or show the whole explorer column (Explorer, Git, Properties...),
    /// as it was. If nothing is left in the column, showing it brings the Explorer back.
    /// </summary>
    public void ToggleExplorer()
    {
        if (_factory.IsExplorerColumnShown && _factory.ExplorerColumnHasTools)
        {
            _factory.HideExplorerColumn();
            return;
        }

        _factory.ShowExplorerColumn();
        if (!_factory.ExplorerColumnHasTools)
            ShowTool(ToolTab.Explorer);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SolutionExplorerOnRight))
            _factory.SetExplorerSide(_settings.SolutionExplorerOnRight);
    }

    // ------------------------------------------------------------- documents -> tabs

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var document in e.NewItems?.OfType<OpenDocument>() ?? [])
        {
            var tab = new DocumentDockable(document, _previewMode, _previewSplit, _previewFirst);
            tab.PropertyChanged += OnTabPropertyChanged;
            _tabs[document] = tab;
            _factory.AddDockable(TargetDocumentDock(), tab);
        }

        var removed = e.Action == NotifyCollectionChangedAction.Reset
            ? _tabs.Keys.Where(document => !_documents.Documents.Contains(document)).ToArray()
            : e.OldItems?.OfType<OpenDocument>().ToArray() ?? [];
        foreach (var document in removed)
        {
            if (!_tabs.Remove(document, out var tab))
                continue;
            tab.PropertyChanged -= OnTabPropertyChanged;
            if (_factory.IsInLayout(tab))
                _factory.RemoveDockable(tab, collapse: true);
            else
                _factory.Detach(tab);
            TabRemoved?.Invoke(tab);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DocumentDockable tab)
            return;
        if (e.PropertyName == nameof(DocumentDockable.PreviewMode))
            _previewMode = tab.PreviewMode;
        else if (e.PropertyName == nameof(DocumentDockable.PreviewSplit))
            _previewSplit = tab.PreviewSplit;
        else if (e.PropertyName == nameof(DocumentDockable.PreviewFirst))
            _previewFirst = tab.PreviewFirst;
    }

    /// <summary>New documents open beside the selected tab (in its window), else in the main window.</summary>
    private IDock TargetDocumentDock()
        => ActiveTab is { Owner: IDocumentDock dock } tab && _factory.IsInLayout(tab) ? dock : _factory.MainDocuments;

    private DocumentDockable? ActiveTab
        => _documents.Active is { } active && _tabs.TryGetValue(active, out var tab) ? tab : null;

    // ------------------------------------------------------------- Active <-> the tab the user is in

    private void OnActiveDocumentChanged(OpenDocument? document)
    {
        if (_syncing || document is null || !_tabs.TryGetValue(document, out var tab))
            return;

        _syncing = true;
        try
        {
            if (!_factory.IsInLayout(tab))
            {
                _factory.Detach(tab);
                _factory.AddDockable(_factory.MainDocuments, tab);
            }

            _factory.SetActiveDockable(tab);
            if (tab.Owner is IDock owner)
            {
                _factory.SetFocusedDockable(owner, tab);
                _lastActiveDock = owner;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The user went to a tab (clicked it, or into its editor), in any window.</summary>
    private void OnDockFocused(IDockable? dockable)
    {
        if (dockable is DocumentDockable tab)
            MakeActive(tab);
    }

    /// <summary>
    /// A tab strip's front tab changed. Only follow it in the strip the user is working in, so closing
    /// a background tab in another window doesn't move the selection there.
    /// </summary>
    private void OnDockActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e)
    {
        if (e.Dockable is DocumentDockable tab
            && (_documents.Active is null || ReferenceEquals(tab.Owner, ActiveTab?.Owner ?? _lastActiveDock)))
            MakeActive(tab);
    }

    private void MakeActive(DocumentDockable tab)
    {
        if (_syncing || !_tabs.ContainsKey(tab.Document))
            return;

        _syncing = true;
        try
        {
            _documents.Active = tab.Document;
            _lastActiveDock = tab.Owner as IDock;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ------------------------------------------------------------- page tabs (diffs, NuGet)

    /// <summary>Bring an open page tab to the front; false when there's no page with that id open.</summary>
    public bool ShowPage(string id)
    {
        if (!_pages.TryGetValue(id, out var page) || !_factory.IsInLayout(page))
            return false;
        Activate(page);
        return true;
    }

    /// <summary>Show a page tab (diff, NuGet manager) beside the selected tab, or bring back the same page if it's open.</summary>
    public void OpenPage(PageDockable page)
    {
        if (_pages.TryGetValue(page.Id, out var open) && _factory.IsInLayout(open))
        {
            page.Dispose();
            page = open;
        }
        else
        {
            if (open is not null)
                ForgetPage(open);
            _pages[page.Id] = page;
            _factory.AddDockable(TargetDocumentDock(), page);
        }

        Activate(page);
    }

    private void Activate(PageDockable page)
    {
        _factory.SetActiveDockable(page);
        if (page.Owner is IDock owner)
            _factory.SetFocusedDockable(owner, page);
    }

    private void OnDockableClosed(object? sender, DockableClosedEventArgs e)
    {
        if (e.Dockable is PageDockable page)
            ForgetPage(page);
    }

    private void ForgetPage(PageDockable page)
    {
        _pages.Remove(page.Id);
        page.Dispose();
        TabRemoved?.Invoke(page);
    }

    // ------------------------------------------------------------- closing

    /// <summary>A tab's close button: ask about unsaved changes first; the tab goes when the document does.</summary>
    private void OnDockableClosing(object? sender, DockableClosingEventArgs e)
    {
        if (_shuttingDown || e.Dockable is not DocumentDockable tab)
            return;

        e.Cancel = true;
        Dispatcher.UIThread.Post(() => _commands.CloseDocumentCommand.Execute(tab.Document));
    }

    /// <summary>A floating window's close button: move its tabs back into the main window, then let it go.</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shuttingDown || e.Window?.Layout is not { } layout || !DockFactory.TabsIn(layout).Any())
            return;

        e.Cancel = true;
        var window = e.Window;
        Dispatcher.UIThread.Post(() => MoveTabsHome(window, layout));
    }

    private void MoveTabsHome(IDockWindow window, IRootDock layout)
    {
        window.Save();   // its current position and size, from the platform window
        foreach (var dockable in DockFactory.TabsIn(layout).ToArray())
        {
            _factory.Detach(dockable);
            if (dockable is ToolDockable tool && _factory.FloatsByDefault(tool))
                _searchWindowBounds = (window.X, window.Y, window.Width, window.Height);   // reopens here
            else if (dockable is ToolDockable docked)
                _factory.PutBack(docked);
            else
                _factory.AddDockable(_factory.MainDocuments, dockable);
        }

        if (Root.Windows?.Contains(window) == true)
            _factory.RemoveWindow(window);

        OnActiveDocumentChanged(_documents.Active);   // re-focus the selected tab in its new place
    }

    // ------------------------------------------------------------- remembering the layout

    private const string SearchWindowKey = "SearchWindow.";

    /// <summary>Dock sizes and the Search window's bounds, by name (see WorkspaceState.Layout).</summary>
    public Dictionary<string, double> CaptureLayout()
    {
        var layout = _factory.CaptureSizes();

        var search = _factory.Tools[ToolTab.Search];
        if (_factory.FindRoot(search, _ => true)?.Window is { } window && _factory.IsInLayout(search))
        {
            window.Save();
            _searchWindowBounds = (window.X, window.Y, window.Width, window.Height);
        }

        if (_searchWindowBounds is var (x, y, width, height))
        {
            layout[SearchWindowKey + "X"] = x;
            layout[SearchWindowKey + "Y"] = y;
            layout[SearchWindowKey + "Width"] = width;
            layout[SearchWindowKey + "Height"] = height;
        }

        return layout;
    }

    public void ApplyLayout(IReadOnlyDictionary<string, double> layout)
    {
        _factory.ApplySizes(layout);
        if (layout.TryGetValue(SearchWindowKey + "X", out var x) && layout.TryGetValue(SearchWindowKey + "Y", out var y)
            && layout.TryGetValue(SearchWindowKey + "Width", out var width) && layout.TryGetValue(SearchWindowKey + "Height", out var height)
            && width > 100 && height > 100)
            _searchWindowBounds = (x, y, width, height);
    }

    /// <summary>The main window is closing: close the floating windows without moving anything.</summary>
    public void CloseFloatingWindows()
    {
        _shuttingDown = true;
        foreach (var window in Root.Windows?.ToArray() ?? [])
            window.Exit();
    }
}
