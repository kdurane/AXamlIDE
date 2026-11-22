using System;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AXamlIDE.Models;

namespace AXamlIDE.Editor.Debugging;

/// <summary>
/// A custom margin that draws document-owned breakpoint markers, and Visual Studio's arrow at the current statement
/// while paused (yellow; green for another frame picked in the Call Stack).
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private readonly IBrush _defaultBackgroundBrush = Brushes.Transparent;
    private IBrush _backgroundBrush = new ImmutableSolidColorBrush(new Color(255, 51, 51, 51));
    private readonly IBrush _pointerOverBrush = new ImmutableSolidColorBrush(new Color(192, 80, 80, 80));
    private readonly IPen _pointerOverPen = new ImmutablePen(new ImmutableSolidColorBrush(new Color(192, 37, 37, 37)));
    private readonly IBrush _markerBrush = new ImmutableSolidColorBrush(new Color(255, 195, 81, 92));
    private readonly IPen _markerPen = new ImmutablePen(new ImmutableSolidColorBrush(new Color(255, 240, 92, 104)));

    private int _pointerOverLine = -1;
    private OpenDocument? _document;
    private (int Line, bool IsTop)? _statement;

    /// <summary>The paused statement's line in this document, and whether it's the top frame's; null for none.</summary>
    public (int Line, bool IsTop)? CurrentStatement
    {
        get => _statement;
        set
        {
            if (_statement == value)
                return;
            _statement = value;
            InvalidateVisual();
        }
    }

    public IBrush BackgroundBrush
    {
        get => _backgroundBrush;
        set
        {
            _backgroundBrush = value;
            InvalidateVisual();
        }
    }

    public void SetDefaultBackgroundBrush()
    {
        _backgroundBrush = _defaultBackgroundBrush;
        InvalidateVisual();
    }

    public BreakpointMargin()
    {
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public void SetDocument(OpenDocument? document)
    {
        _document?.BreakpointsChanged -= OnBreakpointsChanged;

        _document = document;

        _document?.BreakpointsChanged += OnBreakpointsChanged;

        InvalidateVisual();
    }

    private void OnBreakpointsChanged() => InvalidateVisual();

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            oldTextView.DocumentChanged -= OnDocumentChanged;
        }

        if (newTextView != null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
            newTextView.DocumentChanged += OnDocumentChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    private void OnVisualLinesChanged(object? sender, EventArgs eventArgs)
    {
        InvalidateVisual();
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(20, 0);
    }

    private int GetLineNumber(PointerEventArgs e)
    {
        double visualY = e.GetPosition(TextView).Y + TextView.VerticalOffset;
        VisualLine visualLine = TextView.GetVisualLineFromVisualTop(visualY);
        return (visualLine == null) ? -1 : visualLine.FirstDocumentLine.LineNumber;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointerOverLine = GetLineNumber(e);
        InvalidateVisual();

        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointerOverLine = -1;
        InvalidateVisual();

        base.OnPointerExited(e);
    }

    /// <summary>The margin's Conditions... item: the editor asks for the condition and hit count (it has the dialogs).</summary>
    public event Action<int>? ConditionsRequested;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        int line = _pointerOverLine = GetLineNumber(e);
        e.Handled = true;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (line > 0)
                ShowMenu(line);
        }
        else
        {
            _document?.ToggleBreakpoint(line);
            InvalidateVisual();
        }

        base.OnPointerPressed(e);
    }

    /// <summary>Visual Studio's breakpoint menu: on a breakpoint, Delete / Disable / Conditions; else Insert.</summary>
    private void ShowMenu(int line)
    {
        if (_document is not { } document)
            return;

        var menu = new Avalonia.Controls.ContextMenu();
        Avalonia.Controls.MenuItem Item(string header, Action action)
        {
            var item = new Avalonia.Controls.MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        if (document.BreakpointAt(line) is { } breakpoint)
        {
            menu.Items.Add(Item("_Delete Breakpoint", () => document.ToggleBreakpoint(line)));
            menu.Items.Add(Item(breakpoint.IsEnabled ? "Disa_ble Breakpoint" : "Ena_ble Breakpoint",
                () => document.SetBreakpointEnabled(line, !breakpoint.IsEnabled)));
            menu.Items.Add(new Avalonia.Controls.Separator());
            menu.Items.Add(Item("_Conditions...", () => ConditionsRequested?.Invoke(line)));
            if (breakpoint.HasCondition)
                menu.Items.Add(Item("Remove Co_nditions", () => document.SetBreakpointConditions(line, null, null)));
        }
        else
        {
            menu.Items.Add(Item("_Insert Breakpoint", () => document.ToggleBreakpoint(line)));
            menu.Items.Add(Item("Insert _Conditional Breakpoint...", () =>
            {
                document.ToggleBreakpoint(line);
                ConditionsRequested?.Invoke(line);
            }));
        }

        menu.Open(this);
    }

    /// <summary>VS's statement arrow: a block arrow pointing right, over the breakpoint (if any).</summary>
    private static void DrawArrow(DrawingContext context, double y, bool isTop)
    {
        var fill = Views.ThemeBrushes.Get(isTop ? "CurrentStatementArrowBrush" : "StackFrameArrowBrush") ?? Brushes.Gold;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(2, y - 3), true);
            g.LineTo(new Point(9, y - 3));
            g.LineTo(new Point(9, y - 7));
            g.LineTo(new Point(17, y));
            g.LineTo(new Point(9, y + 7));
            g.LineTo(new Point(9, y + 3));
            g.LineTo(new Point(2, y + 3));
            g.EndFigure(true);
        }
        context.DrawGeometry(fill, new Pen(Brushes.Black, 0.8), geometry);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(_backgroundBrush, null, Bounds);

        if (TextView?.VisualLinesValid == true)
        {
            var breakpoints = _document?.Breakpoints;   // once per render, not per line
            // VS's dot is about two thirds of the line height; it grows and shrinks with the font.
            var radius = Math.Clamp(TextView.DefaultLineHeight * 0.34, 4, 8);
            foreach (var visualLine in TextView.VisualLines)
            {
                double y = visualLine.VisualTop - TextView.VerticalOffset + visualLine.Height / 2;
                var centre = new Point(10, y);

                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                if (breakpoints?.FirstOrDefault(b => b.Line == lineNumber) is { } breakpoint)
                {
                    // VS: a filled dot; hollow when disabled; a white plus inside when it has Conditions.
                    if (breakpoint.IsEnabled)
                        context.DrawEllipse(_markerBrush, _markerPen, centre, radius, radius);
                    else
                        context.DrawEllipse(null, new Pen(_markerPen.Brush, 1.5), centre, radius - 0.75, radius - 0.75);
                    if (breakpoint.HasCondition)
                    {
                        var arm = radius / 2;
                        var plus = new Pen(breakpoint.IsEnabled ? Brushes.White : _markerPen.Brush, 1.5);
                        context.DrawLine(plus, new Point(10 - arm, y), new Point(10 + arm, y));
                        context.DrawLine(plus, new Point(10, y - arm), new Point(10, y + arm));
                    }
                }
                else if (_pointerOverLine == lineNumber)
                    context.DrawEllipse(_pointerOverBrush, _pointerOverPen, centre, radius, radius);

                if (_statement is { } statement && statement.Line == visualLine.FirstDocumentLine.LineNumber)
                    DrawArrow(context, y, statement.IsTop);
            }
        }

        base.Render(context);
    }
}
