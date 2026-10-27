using System;
using System.Collections.Generic;
using System.Linq;

namespace AXamlIDE.Models;

/// <summary>
/// The bottom panel (Problems, Output, Debug Console...) as it was left, so it comes back that way: whether it's
/// showing, and which of the tabs that can be closed (Terminal, Test Explorer) are open, by ToolTab id.
///
/// Saved as it is in settings.json ("Panel"); a value missing from the file keeps its default.
/// </summary>
public sealed record PanelSettings
{
    public bool Visible { get; init; } = true;

    public IReadOnlyList<string> OpenTools { get; init; } = [];

    public bool Equals(PanelSettings? other)
        => other is not null && Visible == other.Visible && OpenTools.SequenceEqual(other.OpenTools, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Visible, OpenTools.Count);
}
