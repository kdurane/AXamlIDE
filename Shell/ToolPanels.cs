using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Messages;
using AXamlIDE.ViewModels.Problems;
using AXamlIDE.ViewModels.Running;
using AXamlIDE.ViewModels.Search;
using AXamlIDE.Views.Problems;
using AXamlIDE.Views.Running;
using AXamlIDE.Views.Search;

namespace AXamlIDE.Views.Shell;

/// <summary>
/// The tool panels shown in the bottom tab strip, in order. To add a panel: write a self-contained
/// view bound to its own view model, add a <see cref="ToolTab"/> id, and add a line here.
/// </summary>
public sealed class ToolPanels
{
    public ToolPanels(ProblemsViewModel problems, RunViewModel run, SearchViewModel search)
    {
        All =
        [
            new ToolPanel(ToolTab.Problems, "Problems", () => new ProblemsView { DataContext = problems }),
            new ToolPanel(ToolTab.Output, "Output", () => new OutputView { DataContext = run }),
            new ToolPanel(ToolTab.Console, "Console", () => new ConsoleView { DataContext = run }),
            new ToolPanel(ToolTab.Search, "Search", () => new SearchView { DataContext = search }),
        ];
    }

    public IReadOnlyList<ToolPanel> All { get; }

    public ToolPanel? Find(string id) => All.FirstOrDefault(panel => panel.Id == id);
}
