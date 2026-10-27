using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AXamlIDE.Views;

/// <summary>
/// Stacks <see cref="CollapsiblePane"/>s top to bottom, as VS Code's side bar does with its sections:
///  - a closed pane is just its header, so closing the last pane drops its header to the bottom;
///  - the open panes share the rest of the height, in proportion to their <see cref="CollapsiblePane.PaneHeight"/>;
///  - dragging the line between two panes resizes the open pane above it against the open pane below it
///    (the cursor shows where it can be dragged).
/// </summary>
public class PaneStack : Panel
{
    /// <summary>An open pane is never squeezed below this (its header plus a few rows).</summary>
    private const double MinimumOpenHeight = 66;

    /// <summary>How far from a line between panes the pointer can be to drag it.</summary>
    private const double GripDistance = 3;

    private readonly List<double> _heights = [];   // each child's height from the last measure

    private (CollapsiblePane Above, CollapsiblePane Below, double StartY, double AboveHeight, double BelowHeight)? _drag;

    static PaneStack()
    {
        AffectsParentMeasure<PaneStack>(CollapsiblePane.IsExpandedProperty, CollapsiblePane.PaneHeightProperty);
    }

    public PaneStack()
    {
        // Tunnelling, so the line can be grabbed even though the panes' own controls cover it.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => _drag = null);
    }

    private IEnumerable<CollapsiblePane> Panes => Children.OfType<CollapsiblePane>().Where(pane => pane.IsVisible);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var panes = Children.ToList();
        _heights.Clear();

        // Closed panes (and anything that isn't a pane) take what they need; open panes share what's left.
        var used = 0.0;
        foreach (var child in panes)
        {
            if (child is CollapsiblePane { IsExpanded: true } || !child.IsVisible)
                continue;
            child.Measure(new Size(width, double.PositiveInfinity));
            used += child.DesiredSize.Height;
        }

        var open = panes.OfType<CollapsiblePane>().Where(pane => pane.IsVisible && pane.IsExpanded).ToList();
        var free = double.IsInfinity(availableSize.Height)
            ? open.Count * MinimumOpenHeight * 3
            : Math.Max(0, availableSize.Height - used);
        var shares = SharesOf(open, free);

        foreach (var child in panes)
        {
            if (!child.IsVisible)
            {
                _heights.Add(0);
                continue;
            }
            if (child is CollapsiblePane { IsExpanded: true } pane)
            {
                var height = shares[open.IndexOf(pane)];
                child.Measure(new Size(width, height));
                _heights.Add(height);
            }
            else
            {
                _heights.Add(child.DesiredSize.Height);
            }
        }

        var desiredWidth = panes.Where(child => child.IsVisible).Select(child => child.DesiredSize.Width).DefaultIfEmpty(0).Max();
        return new Size(desiredWidth, double.IsInfinity(availableSize.Height) ? _heights.Sum() : availableSize.Height);
    }

    /// <summary>
    /// The heights of the open panes: <paramref name="free"/> shared in proportion to what each asks for (a pane that
    /// hasn't asked gets an even share), none below <see cref="MinimumOpenHeight"/> when there's room for that.
    /// </summary>
    private static double[] SharesOf(List<CollapsiblePane> open, double free)
    {
        if (open.Count == 0)
            return [];

        var even = free / open.Count;
        var wanted = open.Select(pane => double.IsNaN(pane.PaneHeight) || pane.PaneHeight <= 0 ? even : pane.PaneHeight).ToArray();
        var total = wanted.Sum();
        var shares = wanted.Select(height => total > 0 ? free * height / total : even).ToArray();

        // Lift any pane below the minimum, taking the difference from the others in proportion.
        var minimum = Math.Min(MinimumOpenHeight, even);
        var shortBy = shares.Where(height => height < minimum).Sum(height => minimum - height);
        if (shortBy > 0)
        {
            var spare = shares.Where(height => height > minimum).Sum(height => height - minimum);
            for (var i = 0; i < shares.Length; i++)
                shares[i] = shares[i] < minimum ? minimum
                    : spare > 0 ? shares[i] - (shares[i] - minimum) / spare * shortBy
                    : shares[i];
        }
        return shares;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        var children = Children.ToList();
        for (var i = 0; i < children.Count && i < _heights.Count; i++)
        {
            children[i].Arrange(new Rect(0, y, finalSize.Width, _heights[i]));
            y += _heights[i];
        }
        return finalSize;
    }

    // ------------------------------------------------------------- dragging the line between two panes

    /// <summary>The open panes either side of the line at <paramref name="y"/>, if the pointer is on one that can move.</summary>
    private (CollapsiblePane Above, CollapsiblePane Below)? GripAt(double y)
    {
        var panes = Panes.ToList();
        for (var i = 1; i < panes.Count; i++)
        {
            var line = panes[i].Bounds.Top;
            if (Math.Abs(y - line) > GripDistance)
                continue;
            var above = panes.Take(i).LastOrDefault(pane => pane.IsExpanded);
            var below = panes.Skip(i).FirstOrDefault(pane => pane.IsExpanded);
            return above is not null && below is not null ? (above, below) : null;
        }
        return null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || GripAt(point.Position.Y) is not var (above, below))
            return;

        // From now on every open pane asks for the height it has, so only these two change while dragging.
        foreach (var pane in Panes.Where(pane => pane.IsExpanded))
            pane.PaneHeight = pane.Bounds.Height;

        _drag = (above, below, point.Position.Y, above.Bounds.Height, below.Bounds.Height);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var y = e.GetPosition(this).Y;
        if (_drag is not { } drag)
        {
            Cursor = GripAt(y) is null ? null : new Cursor(StandardCursorType.SizeNorthSouth);
            return;
        }

        var both = drag.AboveHeight + drag.BelowHeight;
        var minimum = Math.Min(MinimumOpenHeight, both / 2);
        var above = Math.Clamp(drag.AboveHeight + y - drag.StartY, minimum, both - minimum);
        drag.Above.PaneHeight = above;
        drag.Below.PaneHeight = both - above;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is null)
            return;
        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
