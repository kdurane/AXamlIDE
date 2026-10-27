using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace AXamlIDE.Views;

/// <summary>
/// One section of a <see cref="PaneStack"/>, like VS Code's side bar sections (Changes, Graph): a header with a
/// chevron that opens and closes it, buttons on the right of the header (<see cref="Actions"/>, shown while open),
/// and the content below. Its look is in Styles/PaneStyles.axaml.
/// </summary>
public class CollapsiblePane : HeaderedContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsiblePane, bool>(nameof(IsExpanded), true, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<CollapsiblePane, object?>(nameof(Actions));

    /// <summary>
    /// The height it wants while open. The <see cref="PaneStack"/> shares its height out between the open panes in
    /// proportion to this, and dragging the line between two panes sets it. NaN (the default) is a fair share.
    /// </summary>
    public static readonly StyledProperty<double> PaneHeightProperty =
        AvaloniaProperty.Register<CollapsiblePane, double>(nameof(PaneHeight), double.NaN, defaultBindingMode: BindingMode.TwoWay);

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public double PaneHeight
    {
        get => GetValue(PaneHeightProperty);
        set => SetValue(PaneHeightProperty, value);
    }
}
