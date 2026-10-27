using System.Collections.Generic;
using AXamlIDE.Models;
using Dock.Model.Mvvm.Controls;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One tool panel tab (Explorer, Problems, Output...) in the dock layout. <see cref="Dock.Model.Core.IDockable.Id"/>
/// is a <see cref="Messages.ToolTab"/> id; Views/DockViewCache shows the panel's view for it.
/// Tools can be moved and floated. Most can't be closed, so they can always be found again; the ones that can
/// (Terminal, Tests, the debugger windows, Properties) come back from the View and Debug menus.
/// </summary>
public sealed class ToolDockable : Tool
{
    public ToolDockable(string id, string title)
    {
        Id = id;
        Title = title;
        CanClose = false;
        CanPin = false;
    }

    public IReadOnlyList<ToolMenuOption> Options { get; init; } = [];
}
