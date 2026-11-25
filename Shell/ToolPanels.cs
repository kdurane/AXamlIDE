using System;
using System.Collections.Generic;
using Avalonia.Controls;
using AXamlIDE.Messages;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;

namespace AXamlIDE.Shell;

/// <summary>
/// The view for each tool panel (by <see cref="ToolTab"/> id). A view is created on first use and
/// then kept, so it keeps its state when its tab is moved between tab strips or windows.
/// Each view gets its own view model as DataContext and must not depend on the window it is in.
/// To add a panel: add a <see cref="ToolTab"/> id, place it in ViewModels/DockFactory.cs, add a line here.
/// </summary>
public sealed class ToolPanels
{
    private readonly Dictionary<string, Func<Control>> _create;
    private readonly Dictionary<string, Control> _views = [];

    public ToolPanels(
        ExplorerViewModel explorer,
        GitViewModel git,
        PropertiesViewModel properties,
        ProblemsViewModel problems,
        RunViewModel run,
        TerminalViewModel terminal,
        RunTerminalViewModel runTerminal,
        TestExplorerViewModel tests,
        SearchViewModel search,
        DebugSideBarViewModel debug)
    {
        _create = new()
        {
            [ToolTab.Explorer] = () => new ExplorerView { DataContext = explorer },
            [ToolTab.Git] = () => new GitView { DataContext = git },
            [ToolTab.Properties] = () => new PropertiesView { DataContext = properties },
            [ToolTab.Problems] = () => new ProblemsView { DataContext = problems },
            [ToolTab.Output] = () => new OutputView { DataContext = run },
            [ToolTab.Console] = () => new ConsoleView { DataContext = run },
            [ToolTab.Terminal] = () => new TerminalView { DataContext = terminal },
            [ToolTab.Run] = () => new RunTerminalView { DataContext = runTerminal },
            [ToolTab.Tests] = () => new TestExplorerView { DataContext = tests },
            [ToolTab.Search] = () => new SearchView { DataContext = search },
            [ToolTab.Debug] = () => new DebugSideBarView { DataContext = debug },
        };
    }

    /// <summary>The panel's view, or null for an unknown id.</summary>
    public Control? ViewFor(string id)
    {
        if (_views.TryGetValue(id, out var view))
            return view;
        if (!_create.TryGetValue(id, out var create))
            return null;
        return _views[id] = create();
    }
}
