using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AXamlIDE.Services.ColorValues;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.ColorSwatches;

/// <summary>
/// The small square before a colour in the code. See-through colours are drawn over a checkerboard, so their
/// alpha shows. Clicking it opens the colour picker for that colour.
/// </summary>
internal sealed class ColorSwatch : Control
{
    private readonly Action<ColorSwatch> _clicked;
    private readonly double _size;
    private readonly double _lineHeight;

    public ColorSwatch(DocumentLine line, ColorLiteral color, double fontSize, double lineHeight, double baseline, Action<ColorSwatch> clicked)
    {
        Line = line;
        Color = color;
        _clicked = clicked;
        _size = Math.Round(fontSize * 0.8);
        _lineHeight = lineHeight;

        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Pick a colour");
        TextBlock.SetBaselineOffset(this, baseline);
    }

    /// <summary>The line and colour it was made for (the column is <see cref="ColorLiteral.Start"/>).</summary>
    public DocumentLine Line { get; }
    public ColorLiteral Color { get; }

    protected override Size MeasureOverride(Size availableSize) => new(_size + 4, _lineHeight);

    public override void Render(DrawingContext context)
    {
        var box = new Rect(0, Math.Round((_lineHeight - _size) / 2), _size, _size);
        var (a, r, g, b) = (Color.Color.A, Color.Color.R, Color.Color.G, Color.Color.B);

        if (a < 255)
        {
            context.FillRectangle(Brushes.White, box);
            var cell = _size / 2;
            context.FillRectangle(Brushes.LightGray, new Rect(box.X, box.Y, cell, cell));
            context.FillRectangle(Brushes.LightGray, new Rect(box.X + cell, box.Y + cell, cell, cell));
        }

        context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromArgb(a, r, g, b)), box);
        // A muted outline (the folding chevrons' colour), so white and black swatches show on any theme.
        context.DrawRectangle(new Pen(ThemeBrushes.Get("FoldingMarkerForegroundBrush") ?? Brushes.Gray), box.Deflate(0.5));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;   // not a caret move
        _clicked(this);
    }
}
