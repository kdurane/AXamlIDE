using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AXaxmlIDE.Helpers;

namespace AXaxmlIDE.Services;

internal sealed class FoldingCaretMargin(FoldingManager foldingManager) : AbstractMargin
{
    private const string ChevronKey = "Icon.ChevronDown";
    private Geometry? _chevron;
    private Point _chevronCenter;
    private const double MarginWidth = 20;
    private const double IconSize = 16;
    private const double IconScale = IconSize / 24; // path is authored on a 24x24 grid

    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.Parse("#8A929E"));
    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.Parse("#E0E5EB"));
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private bool _isMarginHovered;
    private int _hoveredLine = -1;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadIcon();
    }

    private void LoadIcon()
    {
        _chevron = IconResources.Get(this, ChevronKey);
        _chevronCenter = _chevron?.Bounds.Center ?? default;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _isMarginHovered = true;
        InvalidateVisual();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var line = GetLineNumber(e);

        // Hand cursor only when actually over a chevron's row, not the whole gutter.
        Cursor = line > 0 && FindFolding(line) is not null ? HandCursor : null;

        if (!_isMarginHovered || line != _hoveredLine)
        {
            _isMarginHovered = true;
            _hoveredLine = line;
            InvalidateVisual();
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _isMarginHovered = false;
        _hoveredLine = -1;
        Cursor = null;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var lineNumber = GetLineNumber(e);
            if (lineNumber > 0 && FindFolding(lineNumber) is { } folding)
            {
                folding.IsFolded = !folding.IsFolded;
                e.Handled = true;
                InvalidateVisual();
            }
        }

        base.OnPointerPressed(e);
    }

    public override void Render(DrawingContext context)
    {
        // Transparent fill so the whole margin is hit-testable (enter/move/press).
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        if (_chevron is null || TextView?.VisualLinesValid != true)
            return;

        var renderedLines = new HashSet<int>();
        foreach (var visualLine in TextView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            if (!renderedLines.Add(lineNumber))
                continue;

            var folding = FindFolding(lineNumber);
            if (folding is null)
                continue;

            // Expanded chevrons only show while the margin is hovered; folded ones always show.
            if (!folding.IsFolded && !_isMarginHovered)
                continue;

            var x = Bounds.Width / 2;
            var y = visualLine.VisualTop - TextView.VerticalOffset + visualLine.Height / 2;
            var brush = lineNumber == _hoveredLine ? HoverBrush : MarkerBrush;

            // Down = 0°, folded = -90° (counter-clockwise, so it points right).
            var angle = folding.IsFolded ? -Math.PI / 2 : 0;

            var transform =
                Matrix.CreateTranslation(-_chevronCenter.X, -_chevronCenter.Y) // centre on origin
                * Matrix.CreateRotation(angle)
                * Matrix.CreateScale(IconScale, IconScale)
                * Matrix.CreateTranslation(x, y);

            using (context.PushTransform(transform))
                context.DrawGeometry(brush, null, _chevron);
        }
    }

    private int GetLineNumber(PointerEventArgs e)
    {
        if (TextView?.VisualLinesValid != true)
            return -1;

        var visualY = e.GetPosition(TextView).Y + TextView.VerticalOffset;
        return TextView.GetVisualLineFromVisualTop(visualY)?.FirstDocumentLine.LineNumber ?? -1;
    }

    private FoldingSection? FindFolding(int lineNumber)
    {
        if (TextView?.Document is not { } document)
            return null;

        var line = document.GetLineByNumber(lineNumber);
        return foldingManager.AllFoldings.FirstOrDefault(folding =>
            folding.StartOffset >= line.Offset && folding.StartOffset <= line.EndOffset);
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
}
