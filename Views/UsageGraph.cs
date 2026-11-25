using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AXamlIDE.Views;

/// <summary>
/// A running graph, as in Visual Studio's Diagnostic Tools: the latest <see cref="Capacity"/> values as a line with the
/// area under it filled, newest at the right edge, so it scrolls left as values are added. The scale goes from 0 to
/// <see cref="Maximum"/>, or to a little above the largest value when that's NaN (so a growing memory graph stays in view).
/// </summary>
public class UsageGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<UsageGraph, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<UsageGraph, int>(nameof(Capacity), 120);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<UsageGraph, double>(nameof(Maximum), double.NaN);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<UsageGraph, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<UsageGraph, IBrush?>(nameof(GridBrush));

    static UsageGraph()
    {
        AffectsRender<UsageGraph>(ValuesProperty, CapacityProperty, MaximumProperty, StrokeProperty, GridBrushProperty);
    }

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <summary>How many values fit across the graph.</summary>
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>The frame and the half-way line.</summary>
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size).Deflate(0.5);
        if (bounds.Width <= 2 || bounds.Height <= 2)
            return;

        if (GridBrush is { } grid)
        {
            var gridPen = new Pen(grid, 1);
            context.DrawRectangle(null, gridPen, bounds);
            var middle = Math.Round(bounds.Center.Y) + 0.5;
            context.DrawLine(new Pen(grid, 1, new DashStyle([2, 3], 0)), new Point(bounds.Left, middle), new Point(bounds.Right, middle));
        }

        if (Values is not { Count: > 0 } values || Stroke is not { } stroke)
            return;

        var top = ScaleTop(values);
        var step = bounds.Width / Math.Max(1, Capacity - 1);
        var first = Math.Max(0, values.Count - Capacity);
        double X(int index) => bounds.Right - (values.Count - 1 - index) * step;
        double Y(double value) => bounds.Bottom - Math.Clamp(value / top, 0, 1) * bounds.Height;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            lineContext.BeginFigure(new Point(X(first), Y(values[first])), isFilled: false);
            areaContext.BeginFigure(new Point(X(first), bounds.Bottom), isFilled: true);
            areaContext.LineTo(new Point(X(first), Y(values[first])));
            for (var i = first + 1; i < values.Count; i++)
            {
                var point = new Point(X(i), Y(values[i]));
                lineContext.LineTo(point);
                areaContext.LineTo(point);
            }
            areaContext.LineTo(new Point(X(values.Count - 1), bounds.Bottom));
            lineContext.EndFigure(isClosed: false);
            areaContext.EndFigure(isClosed: true);
        }

        using (context.PushOpacity(0.25))
            context.DrawGeometry(stroke, null, area);
        context.DrawGeometry(null, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round), line);
    }

    /// <summary>The value at the top of the graph: <see cref="Maximum"/>, or the largest value rounded up a step.</summary>
    private double ScaleTop(IReadOnlyList<double> values)
    {
        if (double.IsFinite(Maximum) && Maximum > 0)
            return Maximum;
        var largest = 0.0;
        foreach (var value in values)
            largest = Math.Max(largest, value);
        return NiceCeiling(largest * 1.1);
    }

    /// <summary>Rounds up to a round number (1, 1.5, 2, 3, 4, 5, 6 or 8 times a power of ten), so the scale doesn't creep with every reading.</summary>
    public static double NiceCeiling(double value)
    {
        if (!(value > 0))
            return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in (double[])[1, 1.5, 2, 3, 4, 5, 6, 8, 10])
            if (value <= step * power)
                return step * power;
        return 10 * power;
    }
}
