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
/// A custom margin that draws document-owned breakpoint markers.
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        int line = _pointerOverLine = GetLineNumber(e);

        _document?.ToggleBreakpoint(line);
        InvalidateVisual();
        e.Handled = true;

        base.OnPointerPressed(e);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(_backgroundBrush, null, Bounds);

        if (TextView?.VisualLinesValid == true)
        {
            foreach (var visualLine in TextView.VisualLines)
            {
                double y = visualLine.VisualTop - TextView.VerticalOffset + visualLine.Height / 2;

                if (_document?.BreakpointLines.Contains(visualLine.FirstDocumentLine.LineNumber) == true)
                    context.DrawEllipse(_markerBrush, _markerPen, new Point(10, y), 8, 8);
                else if (_pointerOverLine == visualLine.FirstDocumentLine.LineNumber)
                    context.DrawEllipse(_pointerOverBrush, _pointerOverPen, new Point(10, y), 8, 8);
            }
        }

        base.Render(context);
    }
}
