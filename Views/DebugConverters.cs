using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Controls;
using Avalonia.Media;

namespace AXamlIDE.Views;

/// <summary>Converters for the debugger value views (DebugValueTreeView).</summary>
public static class DebugConverters
{
    /// <summary>A row's depth as its indentation.</summary>
    public static readonly IValueConverter Indent =
        new FuncValueConverter<int, Thickness>(level => new Thickness(level * 14, 0, 0, 0));

    /// <summary>The expander's chevron: down when expanded, right when not.</summary>
    public static readonly IValueConverter Chevron = new FuncValueConverter<bool, Geometry?>(expanded =>
        Application.Current?.FindResource(expanded ? "Icon.ChevronDown" : "Icon.ChevronRight") as Geometry);
}
