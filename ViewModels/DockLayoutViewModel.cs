using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Commands;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
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
///   - <see cref="DocumentManager.Active"/> and the tab the user is in follow each other, in any window; with a page
///     tab (a diff, NuGet) in front there is no active document, so Save / Format don't act on a hidden editor;
///   - closing a tab goes through <see cref="DocumentCommands"/> (so unsaved changes are asked about);
///   - closing a floating window moves its tabs back into the main window rather than losing them;
///   - <see cref="ShowToolTabMessage"/> brings a tool panel to the front (putting it back if needed);
///     Search opens in its own window, which remembers where it was;
///   - the bottom panel comes back as it was left (showing or not, Terminal and Tests open or not: Settings.Panel);
///   - the side bar has a Debug tab while debugging (VS Code's Run and Debug view);
///   - the layout's sizes can be captured and re-applied (WorkspaceSession keeps them per workspace).
///
/// Nothing else talks to the dock model; the views (DockControl, DockViewCache) only show it.
/// </summary>
public sealed class DockLayoutViewModel : IRecipient<ShowToolTabMessage>, IRecipient<DebugSessionChangedMessage>, IRecipient<RunSessionChangedMessage>
{
    /// <summary>
    /// The bottom panel's tabs that can be closed and are remembered (Settings.Panel.OpenTools). Not Run: it comes
    /// with a running program and goes when it ends (an empty one from last time would be no use).
    /// </summary>
    private static readonly string[] OptionalTools = [ToolTab.Terminal, ToolTab.Tests];

    private readonly DockFactory _factory;
    private readonly DocumentManager _documents;
    private readonly DocumentCommands _commands;
    private readonly SettingsViewModel _settings;
    private readonly TabActions _tabActions;
    private readonly Dictionary<OpenDocument, DocumentDockable> _tabs = [];
    private readonly Dictionary<string, PageDockable> _pages = [];
    private IDock? _lastActiveDock;
    private IDocumentDock? _lastMainStrip;   // the main window's tab strip the user was last in (new tabs go there)
    private PageDockable? _activePage;   // the page tab the user is in (then DocumentManager.Active is null)
    private PreviewMode _previewMode = PreviewMode.Split;      // new AXAML tabs start like the last one changed
    private PreviewSplit _previewSplit = PreviewSplit.SideBySide;
    private bool _previewFirst;
    private bool _syncing;
    private bool _shuttingDown;
    private bool _debugging;
    private bool _hidePanelAfterDebugging;
    private PanelBeforeRun? _panelBeforeRun;   // how the bottom panel was when Run started (put back when it ends)
    private bool _hideSideBarAfterDebugging;
    private IDockable? _sideBarTabBeforeDebugging;   // what the side bar goes back to when the Debug tab leaves
    private (double X, double Y, double Width, double Height)? _searchWindowBounds;

    public DockLayoutViewModel(
        DocumentManager documents,
        DocumentCommands commands,
        SettingsViewModel settings,
        WorkspaceViewModel workspace,
        IClipboardService clipboard,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _documents = documents;
        _commands = commands;
        _settings = settings;
        _tabActions = new TabActions(commands, clipboard, dialogs);

        _factory = new DockFactory(new WelcomeViewModel(workspace));
        Root = _factory.CreateLayout(settings.SolutionExplorerOnRight);
        _factory.InitLayout(Root);
        RestorePanel(settings.Panel);

        _documents.Documents.CollectionChanged += OnDocumentsChanged;
        _documents.ActiveChanged += OnActiveDocumentChanged;
        _documents.FrontRequested += OnActiveDocumentChanged;
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
        if (ToolTab.IsDebugSection(id))
            id = ToolTab.Debug;   // Locals, Watch... are sections of the Debug tab (DebugSideBarViewModel opens them)
        if (!_factory.Tools.TryGetValue(id, out var tool))
            return;

        if (_factory.FloatsByDefault(tool) && !_factory.IsInLayout(tool))
        {
            var (mainX, mainY, mainWidth, mainHeight) = MainWindowBounds;
            var (x, y, width, height) = _searchWindowBounds
                ?? (mainX + mainWidth / 2 - 350, mainY + 80, 700, Math.Min(560, mainHeight - 120));
            _factory.FloatTool(tool, x, y, width, height);
        }
        else
        {
            _factory.PutBack(tool);
        }

        _factory.SetActiveDockable(tool);
        if (_factory.FindRoot(tool, _ => true)?.Window is { } window)
            window.SetActive();   // bring its floating window to the front
        SavePanel();
    }

    /// <summary>Whether a tool panel is showing (in a window that's open).</summary>
    public bool IsToolShown(string id) => _factory.Tools.TryGetValue(id, out var tool) && _factory.IsInLayout(tool);

    /// <summary>
    /// The activity bar's explorer button: hide or show the whole explorer column (Explorer, Git, Properties...),
    /// as it was. If nothing is left in the column, showing it brings the Explorer back.
    /// </summary>
    public void ToggleExplorer()
    {
        _hideSideBarAfterDebugging = false;   // shown or hidden on purpose: leave it so after debugging
        if (_factory.IsExplorerColumnShown && _factory.ExplorerColumnHasTools)
        {
            _factory.HideExplorerColumn();
            return;
        }

        _factory.ShowExplorerColumn();
        if (!_factory.ExplorerColumnHasTools)
            ShowTool(ToolTab.Explorer);
    }

    /// <summary>
    /// VS Code's Toggle Panel: hide or show the bottom panel (Problems, Output, Terminal...) as it was. If nothing is
    /// left in it, showing it brings the Output back.
    /// </summary>
    public void TogglePanel()
    {
        if (_factory.IsBottomPanelShown)
        {
            _factory.HideBottomPanel();
            _hidePanelAfterDebugging = false;   // hidden on purpose: no need to hide it again
            _panelBeforeRun = null;   // nor to put it back after Run
            SavePanel();
            return;
        }

        _factory.ShowBottomPanel();
        _hidePanelAfterDebugging = false;
        if (!_factory.BottomPanelHasTools)
            ShowTool(ToolTab.Output);
        SavePanel();
    }

    /// <summary>
    /// Debugging started: the Debug tab joins the side bar, in front (VS Code's Run and Debug view). A hidden side
    /// bar stays hidden until the program first pauses (Locals is shown then). The Debug Console brings up the
    /// bottom panel. Debugging ended: the Debug tab leaves, the side bar goes back to the tab it showed before, and
    /// the side bar and bottom panel are hidden again if debugging is what showed them.
    /// </summary>
    public void Receive(DebugSessionChangedMessage message)
    {
        if (message.IsDebugging == _debugging)
            return;
        _debugging = message.IsDebugging;
        var debug = _factory.Tools[ToolTab.Debug];

        if (_debugging)
        {
            _hidePanelAfterDebugging = !_factory.IsBottomPanelShown;
            _hideSideBarAfterDebugging = !_factory.IsExplorerColumnShown;
            if (!_factory.IsOpen(debug))
                _sideBarTabBeforeDebugging = (_factory.Tools[ToolTab.Explorer].Owner as IDock)?.ActiveDockable;
            _factory.AddToSideBar(debug);
            _factory.SetActiveDockable(debug);
            return;
        }

        var sideBar = debug.Owner as IDock;
        var wasInFront = ReferenceEquals(sideBar?.ActiveDockable, debug);
        _factory.RemoveTool(debug);
        if (sideBar is { VisibleDockables: { Count: > 0 } tabs } && (wasInFront || !tabs.Contains(sideBar.ActiveDockable!)))
            sideBar.ActiveDockable = _sideBarTabBeforeDebugging is { } before && tabs.Contains(before) ? before : tabs[0];
        _sideBarTabBeforeDebugging = null;
        if (_hidePanelAfterDebugging)
            _factory.HideBottomPanel();
        if (_hideSideBarAfterDebugging && _factory.IsExplorerColumnShown)
            _factory.HideExplorerColumn();
        _hidePanelAfterDebugging = false;
        _hideSideBarAfterDebugging = false;
    }

    /// <summary>The bottom panel as Run found it: whether it was showing, and the tab in front.</summary>
    private sealed record PanelBeforeRun(bool PanelShown, IDockable? FrontTab);

    /// <summary>
    /// Run (without debugging) started: remember the bottom panel. It ended: the Run tab goes (only running a program
    /// brings it) and the panel is put back as it was, hidden again if it was hidden (as after debugging). Not when the program failed, or its
    /// build did: the output is what the user needs to see then.
    /// </summary>
    public void Receive(RunSessionChangedMessage message)
    {
        var run = _factory.Tools[ToolTab.Run];
        var panel = _factory.Tools[ToolTab.Output].Owner as IDock;
        if (message.IsRunning)
        {
            var inFront = panel?.ActiveDockable;
            _panelBeforeRun = new PanelBeforeRun(_factory.IsBottomPanelShown, ReferenceEquals(inFront, run) ? null : inFront);
            return;
        }

        if (_panelBeforeRun is not { } before || message.KeepOutput)
        {
            _panelBeforeRun = null;
            return;
        }
        _panelBeforeRun = null;

        _factory.RemoveTool(run);
        if (before.FrontTab is { } front && panel?.VisibleDockables?.Contains(front) == true)
            panel.ActiveDockable = front;
        if (!before.PanelShown && !_debugging)
            _factory.HideBottomPanel();
        SavePanel();
    }

    /// <summary>Open the remembered optional tabs, then hide the panel if it was left hidden.</summary>
    private void RestorePanel(PanelSettings panel)
    {
        foreach (var id in panel.OpenTools.Intersect(OptionalTools))
            _factory.PutBack(_factory.Tools[id]);
        if (!panel.Visible)
            _factory.HideBottomPanel();
    }

    /// <summary>Remember the bottom panel as the user left it (not while debugging, which changes it for a while).</summary>
    private void SavePanel()
    {
        if (_debugging || _shuttingDown)
            return;
        _settings.Panel = new PanelSettings
        {
            Visible = _factory.IsBottomPanelShown,
            OpenTools = [.. OptionalTools.Where(id => _factory.IsOpen(_factory.Tools[id]))]
        };
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
            var tab = new DocumentDockable(document, _tabActions, _previewMode, _previewSplit, _previewFirst);
            tab.PropertyChanged += OnTabPropertyChanged;
            _tabs[document] = tab;
            _factory.AddDockable(TargetDocumentDock(), tab);
        }

        var removed = e.Action == NotifyCollectionChangedAction.Reset
            ? [.. _tabs.Keys.Where(document => !_documents.Documents.Contains(document))]
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

    /// <summary>
    /// New documents open in the main window, as in Visual Studio: beside the selected tab when it's there, else in
    /// the main window's strip the user was last in (with the editor split), else the main strip. A tab dragged out to
    /// its own window keeps that window to itself.
    /// </summary>
    private IDock TargetDocumentDock()
    {
        if (((IDockable?)ActiveTab ?? _activePage) is { Owner: IDocumentDock dock } && _factory.IsInMainWindow(dock))
            return dock;
        return _lastMainStrip is { } strip && _factory.IsInMainWindow(strip) ? strip : _factory.MainDocuments;
    }

    /// <summary>The tab strip the user is working in now (see <see cref="IsWorkingStrip"/>).</summary>
    private void RememberStrip(IDock? dock)
    {
        _lastActiveDock = dock;
        if (dock is IDocumentDock strip && _factory.IsInMainWindow(strip))
            _lastMainStrip = strip;
    }

    private DocumentDockable? ActiveTab
        => _documents.Active is { } active && _tabs.TryGetValue(active, out var tab) ? tab : null;

    // ------------------------------------------------------------- Active <-> the tab the user is in

    private void OnActiveDocumentChanged(OpenDocument? document)
    {
        if (_syncing || document is null || !_tabs.TryGetValue(document, out var tab))
            return;
        _activePage = null;

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
                RememberStrip(owner);
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
        else if (dockable is PageDockable page)
            MakePageActive(page);
    }

    /// <summary>
    /// A tab strip's front tab changed. Only follow it in the strip the user is working in, so closing
    /// a background tab in another window doesn't move the selection there.
    /// </summary>
    private void OnDockActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e)
    {
        if (e.Dockable is DocumentDockable tab
            && ((_documents.Active is null && _activePage is null) || IsWorkingStrip(tab.Owner)))
            MakeActive(tab);
        else if (e.Dockable is PageDockable page && IsWorkingStrip(page.Owner))
            MakePageActive(page);
    }

    private bool IsWorkingStrip(IDockable? owner) => ReferenceEquals(owner, ActiveTab?.Owner ?? _lastActiveDock);

    private void MakeActive(DocumentDockable tab)
    {
        if (_syncing || !_tabs.ContainsKey(tab.Document))
            return;

        _syncing = true;
        try
        {
            _activePage = null;
            _documents.Active = tab.Document;
            RememberStrip(tab.Owner as IDock);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The user went to a page tab: no document is active while it's in front.</summary>
    private void MakePageActive(PageDockable page)
    {
        if (_syncing || !_pages.ContainsValue(page))
            return;

        _syncing = true;
        try
        {
            _activePage = page;
            _documents.Active = null;
            RememberStrip(page.Owner as IDock);
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

    /// <summary>Close every page tab (diffs, NuGet, Publish...): they belong to the workspace that's closing.</summary>
    public void CloseAllPages()
    {
        foreach (var page in _pages.Values.ToArray())
        {
            if (_factory.IsInLayout(page))
                _factory.RemoveDockable(page, collapse: true);
            ForgetPage(page);
        }
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
        else if (e.Dockable is ToolDockable)
            SavePanel();   // a tab's X (Terminal, Tests)
    }

    private void ForgetPage(PageDockable page)
    {
        _pages.Remove(page.Id);
        if (ReferenceEquals(_activePage, page))
            _activePage = null;
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

    /// <summary>
    /// A floating window's close button closes the tabs in it, as in Visual Studio: documents (asking about unsaved
    /// changes; Cancel keeps the window open), pages, and tools (hidden: the View menu brings them back home).
    /// </summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shuttingDown || e.Window?.Layout is not { } layout || !DockFactory.TabsIn(layout).Any())
            return;

        e.Cancel = true;
        var window = e.Window;
        Dispatcher.UIThread.Post(() => _ = CloseWindowTabsAsync(window, layout));
    }

    private async Task CloseWindowTabsAsync(IDockWindow window, IRootDock layout)
    {
        window.Save();   // its current position and size, from the platform window

        var documents = DockFactory.TabsIn(layout).OfType<DocumentDockable>().Select(tab => tab.Document).ToArray();
        if (documents.Length > 0 && !await _commands.CloseDocumentsAsync(documents))
            return;   // cancelled: the window stays as it was

        foreach (var dockable in DockFactory.TabsIn(layout).ToArray())
        {
            _factory.Detach(dockable);
            if (dockable is PageDockable page)
                ForgetPage(page);
            else if (dockable is ToolDockable tool && _factory.FloatsByDefault(tool))
                _searchWindowBounds = (window.X, window.Y, window.Width, window.Height);   // reopens here
        }

        if (Root.Windows?.Contains(window) == true)
            _factory.RemoveWindow(window);

        SavePanel();
        OnActiveDocumentChanged(_documents.Active);   // re-focus the selected tab, now in another window
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

    /// <summary>
    /// Window > Reset Window Layout (Visual Studio's): tabs in floating windows come back to the main window
    /// (documents and diffs into its document tabs, tools to their own docks), tools dragged elsewhere go home,
    /// every dock gets its first size back, and the side bar and bottom panel are shown. Which tools are open, which
    /// side the side bar is on, and document tabs split within the main window are kept; Search keeps its window.
    /// </summary>
    public void ResetLayout()
    {
        foreach (var window in Root.Windows?.ToArray() ?? [])
        {
            if (window.Layout is not { } layout)
                continue;
            window.Save();
            var keep = false;
            foreach (var tab in DockFactory.TabsIn(layout).ToArray())
            {
                if (tab is ToolDockable search && _factory.FloatsByDefault(search))
                {
                    keep = true;
                    continue;
                }
                _factory.Detach(tab);
                if (tab is ToolDockable tool)
                    _factory.PutBack(tool);
                else
                    _factory.AddDockable(_factory.MainDocuments, tab);
            }
            if (!keep && Root.Windows?.Contains(window) == true)
                _factory.RemoveWindow(window);
        }

        foreach (var tool in _factory.Tools.Values)
            if (_factory.IsInLayout(tool) && !_factory.IsAtHome(tool))
            {
                _factory.RemoveTool(tool);
                _factory.PutBack(tool);
            }

        if (!_factory.IsExplorerColumnShown)
            ToggleExplorer();
        if (!_factory.IsBottomPanelShown)
            TogglePanel();
        _factory.ResetSizes();   // last: putting docks back shares out the sizes again
        SavePanel();
        OnActiveDocumentChanged(_documents.Active);   // its tab may have moved
    }

    /// <summary>The main window is closing: close the floating windows without moving anything.</summary>
    public void CloseFloatingWindows()
    {
        _shuttingDown = true;
        foreach (var window in Root.Windows?.ToArray() ?? [])
            window.Exit();
    }
}
