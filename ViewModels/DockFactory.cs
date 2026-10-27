using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Messages;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Builds the starting dock layout and knows where each tool panel lives when it isn't moved:
///
///   [Explorer, Git]  | [ Documents (each AXAML tab has its own preview)  ]
///   [Properties]     | [ Problems, Output, Debug Console (+ Terminal, Tests, Locals, Watch, Call Stack) ]
///
/// (Properties only while it's open; the explorer column is mirrored to the right by the "explorer on right" setting).
/// The bottom row is VS Code's panel: it can be hidden and shown as a whole (<see cref="HideBottomPanel"/>). Problems,
/// Output and the Debug Console are always in it; Terminal and Tests join it when opened and leave with their tab's X,
/// as in Visual Studio, and Locals, Watch and Call Stack join it while debugging. Search has no dock: it
/// opens in its own floating window (it can still be dragged into a dock).
/// The user can then drag any tab anywhere, including out into its own window.
/// To add a tool panel: add a <see cref="ToolTab"/> id, add it to a dock here, and give it a view
/// in Shell/ToolPanels.cs.
/// </summary>
public sealed class DockFactory(WelcomeViewModel welcome) : Factory
{
    private readonly WelcomeViewModel _welcome = welcome;
    private readonly Dictionary<string, ToolHome> _homes = [];

    /// <summary>Where a tool goes back to: its dock, and where that dock sits if it has to be re-created.</summary>
    private sealed record ToolHome(IToolDock Dock, IDock Container, bool AtStart);

    public IRootDock Root { get; private set; } = null!;

    /// <summary>The main window's document tabs. Never removed, so new documents always have a home.</summary>
    public IDocumentDock MainDocuments { get; private set; } = null!;

    /// <summary>The top-level row: explorer column, splitter, everything else.</summary>
    public IProportionalDock MainRow { get; private set; } = null!;

    /// <summary>The explorer's side: the Explorer/Git tabs, and the Properties panel below them when it's open.</summary>
    public IProportionalDock ExplorerColumn { get; private set; } = null!;

    private bool _explorerOnRight;

    /// <summary>The documents column: the document tabs over the bottom panel.</summary>
    private ProportionalDock _centre = null!;

    /// <summary>The bottom panel (Problems, Output, Terminal...), and whether the user has hidden it.</summary>
    private ToolDock _bottomPanel = null!;
    private bool _bottomPanelHidden;

    public IReadOnlyDictionary<string, ToolDockable> Tools { get; private set; } = new Dictionary<string, ToolDockable>();

    public override IRootDock CreateLayout() => CreateLayout(explorerOnRight: false);

    public IRootDock CreateLayout(bool explorerOnRight)
    {
        // The explorer side bar stays put (moved with the swap-sides button), so it can't be dragged,
        // floated or have other tabs dropped into it. Tools added to its dock later (e.g. Git) become tabs there.
        var explorer = new ToolDockable(ToolTab.Explorer, "Explorer") { CanDrag = false, CanFloat = false };
        var git = new ToolDockable(ToolTab.Git, "Git") { CanDrag = false, CanFloat = false };
        // Opened from the explorer's right-click menu, under the explorer; closed with its X (as in Visual Studio).
        var properties = new ToolDockable(ToolTab.Properties, "Properties") { CanDrag = false, CanFloat = false, CanClose = true };
        var problems = new ToolDockable(ToolTab.Problems, "Problems");
        var output = new ToolDockable(ToolTab.Output, "Output");
        var console = new ToolDockable(ToolTab.Console, "Debug Console");
        var terminal = new ToolDockable(ToolTab.Terminal, "Terminal") { CanClose = true };
        var tests = new ToolDockable(ToolTab.Tests, "Tests") { CanClose = true };
        var search = new ToolDockable(ToolTab.Search, "Search");
        var locals = new ToolDockable(ToolTab.Locals, "Locals") { CanClose = true };
        var watch = new ToolDockable(ToolTab.Watch, "Watch") { CanClose = true };
        var callStack = new ToolDockable(ToolTab.CallStack, "Call Stack") { CanClose = true };
        Tools = new[] { explorer, git, properties, problems, output, console, terminal, tests, search, locals, watch, callStack }
            .ToDictionary(tool => tool.Id);

        MainDocuments = new DocumentDock
        {
            Id = "Documents",
            Title = "Documents",
            IsCollapsable = false,
            CanCreateDocument = false,
            EnableWindowDrag = true,
            EmptyContent = _welcome,
            VisibleDockables = CreateList<IDockable>()
        };

        var explorerDock = Locked(ToolDock("ExplorerTools", explorerOnRight ? Alignment.Right : Alignment.Left, double.NaN, explorer, git));
        var propertiesDock = Locked(ToolDock("PropertiesTools", Alignment.Bottom, 0.25, properties));   // shown on demand
        ExplorerColumn = Row("ExplorerColumn", Orientation.Vertical, explorerDock);
        ExplorerColumn.Proportion = 0.2;
        var bottomDock = ToolDock("BottomTools", Alignment.Bottom, 0.25, problems, output, console);
        bottomDock.ActiveDockable = output;

        var centre = Row("Centre", Orientation.Vertical, MainDocuments, new ProportionalDockSplitter(), bottomDock);
        _centre = centre;
        _bottomPanel = bottomDock;
        _bottomPanelHidden = false;
        MainRow = explorerOnRight
            ? Row("MainRow", Orientation.Horizontal, centre, new ProportionalDockSplitter(), ExplorerColumn)
            : Row("MainRow", Orientation.Horizontal, ExplorerColumn, new ProportionalDockSplitter(), centre);
        _explorerOnRight = explorerOnRight;

        _homes.Clear();
        _homes[explorer.Id] = new ToolHome(explorerDock, ExplorerColumn, AtStart: true);
        _homes[git.Id] = new ToolHome(explorerDock, ExplorerColumn, AtStart: true);
        _homes[properties.Id] = new ToolHome(propertiesDock, ExplorerColumn, AtStart: false);
        foreach (var tool in new[] { problems, output, console, terminal, tests, locals, watch, callStack })
            _homes[tool.Id] = new ToolHome(bottomDock, centre, AtStart: false);

        var root = CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(MainRow);
        root.ActiveDockable = MainRow;
        root.DefaultDockable = MainRow;
        root.LeftPinnedDockables = CreateList<IDockable>();
        root.RightPinnedDockables = CreateList<IDockable>();
        root.TopPinnedDockables = CreateList<IDockable>();
        root.BottomPinnedDockables = CreateList<IDockable>();
        Root = root;
        return root;
    }

    public override IDockWindow? CreateWindowFrom(IDockable dockable)
    {
        var window = base.CreateWindowFrom(dockable);
        window?.Title = dockable is ToolDockable tool ? tool.Title ?? "AXamlIDE" : "AXamlIDE";
        return window;
    }

    // ------------------------------------------------------------- queries

    /// <summary>
    /// True when the dockable is shown somewhere: in the main window or a floating window that is
    /// still open (not hidden, and not in a dock that was collapsed or a window that was closed).
    /// </summary>
    public bool IsInLayout(IDockable dockable)
    {
        var current = dockable;
        while (current.Owner is IDock owner)
        {
            if (owner.VisibleDockables?.Contains(current) != true)
                return false;
            current = owner;
        }

        return ReferenceEquals(current, Root)
               || Root.Windows?.Any(window => ReferenceEquals(window.Layout, current)) == true;
    }

    /// <summary>All document and tool tabs inside <paramref name="dock"/> (at any depth).</summary>
    public static IEnumerable<IDockable> TabsIn(IDockable dock)
    {
        if (dock is DocumentDockable or ToolDockable)
        {
            yield return dock;
            yield break;
        }

        if (dock is IDock { VisibleDockables: { } children })
            foreach (var child in children.ToArray())
                foreach (var tab in TabsIn(child))
                    yield return tab;
    }

    // ------------------------------------------------------------- tools

    /// <summary>True for tools that live in a floating window rather than a dock (Search).</summary>
    public bool FloatsByDefault(ToolDockable tool) => !_homes.ContainsKey(tool.Id);

    /// <summary>Open a tool that isn't in the layout in its own window, at the given screen bounds.</summary>
    public void FloatTool(ToolDockable tool, double x, double y, double width, double height)
    {
        Detach(tool);

        // SplitToWindow moves a dockable out of a dock, so give it a scratch one (never shown) to come from.
        var scratch = new ToolDock { Id = "Scratch", Owner = Root, VisibleDockables = CreateList<IDockable>() };
        AddDockable(scratch, tool);
        SplitToWindow(scratch, tool, x, y, width, height);
    }

    /// <summary>
    /// Puts a tool that is no longer in the layout back where it started, re-creating its dock if that
    /// was collapsed. If the area it lived in has been rearranged away, it goes into a floating window.
    /// Tools that float by default are left alone (see <see cref="FloatTool"/>).
    /// </summary>
    public void PutBack(ToolDockable tool)
    {
        // Still in the hidden bottom panel: show the panel, the tab stays where it was.
        if (_bottomPanelHidden && ReferenceEquals(tool.Owner, _bottomPanel) && _bottomPanel.VisibleDockables?.Contains(tool) == true)
        {
            ShowBottomPanel();
            return;
        }

        if (IsInLayout(tool) || !_homes.TryGetValue(tool.Id, out var home))
            return;
        Detach(tool);

        if (ReferenceEquals(home.Dock, _bottomPanel) && _bottomPanelHidden)
        {
            ShowBottomPanel();
        }
        else if (!IsInLayout(home.Dock))
        {
            if (!IsInLayout(home.Container))
            {
                AddDockable(MainDocuments, tool);   // somewhere visible to float it from
                FloatDockable(tool);
                return;
            }

            home.Dock.VisibleDockables = CreateList<IDockable>();
            var index = home.AtStart ? 0 : home.Container.VisibleDockables!.Count;
            InsertDockable(home.Container, new ProportionalDockSplitter(), index);
            InsertDockable(home.Container, home.Dock, index + (home.AtStart ? 0 : 1));
        }

        AddDockable(home.Dock, tool);
    }

    // ------------------------------------------------------------- the bottom panel

    /// <summary>The bottom panel is showing, with at least one tool in it.</summary>
    public bool IsBottomPanelShown => IsInLayout(_bottomPanel) && _bottomPanel.VisibleDockables is { Count: > 0 };

    /// <summary>The tool is open: showing, or a tab in the hidden bottom panel.</summary>
    public bool IsOpen(ToolDockable tool)
        => IsInLayout(tool) || (_bottomPanelHidden && _bottomPanel.VisibleDockables?.Contains(tool) == true);

    /// <summary>The bottom panel has at least one tool in it (shown or not).</summary>
    public bool BottomPanelHasTools => _bottomPanel.VisibleDockables is { Count: > 0 };

    /// <summary>
    /// Take the bottom panel (Problems, Output, Terminal... and whatever else is docked there) out of the documents
    /// column: VS Code's Toggle Panel. It keeps its tabs, the selected one and its size, so
    /// <see cref="ShowBottomPanel"/> brings it back as it was.
    /// </summary>
    public void HideBottomPanel()
    {
        if (_centre.VisibleDockables is not { } column)
            return;
        var index = column.IndexOf(_bottomPanel);
        if (index < 0)
            return;

        column.RemoveAt(index);
        // The splitter that was between it and the documents.
        if (index > 0 && column[index - 1] is IProportionalDockSplitter)
            column.RemoveAt(index - 1);
        _bottomPanelHidden = true;
    }

    /// <summary>Put the bottom panel back under the documents (empty if every tool left it; see PutBack).</summary>
    public void ShowBottomPanel()
    {
        _bottomPanelHidden = false;
        if (IsInLayout(_bottomPanel) || _centre.VisibleDockables is not { } column)
            return;

        _bottomPanel.VisibleDockables ??= CreateList<IDockable>();
        InsertDockable(_centre, new ProportionalDockSplitter(), column.Count);
        InsertDockable(_centre, _bottomPanel, column.Count);
    }

    // ------------------------------------------------------------- the explorer column

    /// <summary>The explorer column (Explorer, Git, Properties and whatever else is docked there) is showing.</summary>
    public bool IsExplorerColumnShown => IsInLayout(ExplorerColumn);

    /// <summary>The explorer column has at least one tool in it.</summary>
    public bool ExplorerColumnHasTools => Descendants(ExplorerColumn).Any(dockable => dockable is ITool);

    /// <summary>
    /// Take the whole explorer column out of the main row (VS Code's "toggle side bar"). The column keeps its
    /// tabs, selection and sizes, so <see cref="ShowExplorerColumn"/> brings it back as it was.
    /// </summary>
    public void HideExplorerColumn()
    {
        if (MainRow.VisibleDockables is not { } row)
            return;
        var index = row.IndexOf(ExplorerColumn);
        if (index < 0)
            return;

        row.RemoveAt(index);
        // The splitter that was between it and the centre.
        var splitterIndex = _explorerOnRight ? index - 1 : index;
        if (splitterIndex >= 0 && splitterIndex < row.Count && row[splitterIndex] is IProportionalDockSplitter)
            row.RemoveAt(splitterIndex);
    }

    /// <summary>Put the explorer column back on its side of the main row.</summary>
    public void ShowExplorerColumn()
    {
        if (IsExplorerColumnShown || MainRow.VisibleDockables is not { } row)
            return;

        if (_explorerOnRight)
        {
            InsertDockable(MainRow, new ProportionalDockSplitter(), row.Count);
            InsertDockable(MainRow, ExplorerColumn, row.Count);
        }
        else
        {
            InsertDockable(MainRow, ExplorerColumn, 0);
            InsertDockable(MainRow, new ProportionalDockSplitter(), 1);
        }
    }

    private static IEnumerable<IDockable> Descendants(IDock dock)
        => (dock.VisibleDockables ?? []).SelectMany(child => child is IDock inner ? [child, .. Descendants(inner)] : new[] { child });

    /// <summary>
    /// Mirror the top-level row, so the explorer (and anything else docked beside it) swaps sides.
    /// Only done while the explorer's dock is still in that row.
    /// </summary>
    public void SetExplorerSide(bool onRight)
    {
        if (_explorerOnRight == onRight)
            return;
        _explorerOnRight = onRight;
        _homes[ToolTab.Explorer].Dock.Alignment = onRight ? Alignment.Right : Alignment.Left;

        if (MainRow.VisibleDockables is not System.Collections.ObjectModel.ObservableCollection<IDockable> row
            || !row.Contains(ExplorerColumn))
            return;
        for (var index = 0; index < row.Count - 1; index++)
            row.Move(row.Count - 1, index);
    }

    // ------------------------------------------------------------- remembering sizes

    /// <summary>The proportions of the named docks that are in the layout (for the workspace state).</summary>
    public Dictionary<string, double> CaptureSizes()
    {
        var sizes = new Dictionary<string, double>();
        foreach (var dock in SizedDocks())
            if (double.IsFinite(dock.Proportion) && IsInLayout(dock))
                sizes[dock.Id] = dock.Proportion;
        return sizes;
    }

    /// <summary>Give the named docks their remembered proportions (names not in the layout are ignored).</summary>
    public void ApplySizes(IReadOnlyDictionary<string, double> sizes)
    {
        foreach (var dock in SizedDocks())
            if (sizes.TryGetValue(dock.Id, out var proportion) && double.IsFinite(proportion) && proportion is > 0 and < 1)
                dock.Proportion = proportion;
    }

    private IEnumerable<IDock> SizedDocks()
    {
        yield return MainDocuments;
        foreach (var dock in Find(dockable => dockable is IDock { Id: "Centre" or "MainRow" or "ExplorerColumn" }).OfType<IDock>())
            yield return dock;
        // The explorer's own dock fills what the Properties panel leaves (its width is the column's).
        foreach (var home in _homes.Values.Select(home => home.Dock).Distinct().Where(dock => dock.Id != "ExplorerTools"))
            yield return home;
    }

    /// <summary>Take a tool out of the layout, wherever it is (a dock left empty goes too). <see cref="PutBack"/> brings it back.</summary>
    public void RemoveTool(ToolDockable tool)
    {
        if (IsInLayout(tool))
            RemoveDockable(tool, collapse: true);
        else
            Detach(tool);
    }

    /// <summary>Take a dockable out of whatever (possibly dead) dock still lists it.</summary>
    public void Detach(IDockable dockable)
    {
        if (dockable.Owner is IDock { VisibleDockables: { } siblings } && siblings.Contains(dockable))
            siblings.Remove(dockable);
        dockable.Owner = null;
    }

    // ------------------------------------------------------------- helpers

    private ToolDock ToolDock(string id, Alignment alignment, double proportion, params ToolDockable[] tools) => new()
    {
        Id = id,
        Alignment = alignment,
        Proportion = proportion,
        ActiveDockable = tools[0],
        VisibleDockables = CreateList<IDockable>(tools)
    };

    /// <summary>The explorer column's docks stay put: no dragging, floating, or dropping other tabs in.</summary>
    private static ToolDock Locked(ToolDock dock)
    {
        dock.CanDrop = false;
        dock.CanDrag = false;
        dock.CanFloat = false;
        return dock;
    }

    private ProportionalDock Row(string id, Orientation orientation, params IDockable[] items) => new()
    {
        Id = id,
        Orientation = orientation,
        IsCollapsable = false,
        VisibleDockables = CreateList(items)
    };
}
