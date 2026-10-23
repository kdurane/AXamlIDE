using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.InlayHints;

/// <summary>
/// One inlay hint ("count:", "List&lt;int&gt;") drawn in the line, as VS Code draws them: a little smaller than the code,
/// dimmed, on a faint rounded background, with a gap on the side the server asks for. It can't be clicked: a click on it
/// goes to the text, so the caret lands there as if the hint weren't there.
/// </summary>
internal sealed class InlayHintControl : Control, ICustomHitTest
{
    private const double Padding = 3;
    private readonly TextView _textView;
    private readonly FormattedText _text;
    private readonly double _lineHeight;
    private readonly double _baseline;
    private readonly double _left;
    private readonly double _right;

    public InlayHintControl(IReadOnlyList<LspInlayHint> hints, TextView textView, FontFamily font, double fontSize)
    {
        _textView = textView;
        _lineHeight = textView.DefaultLineHeight;
        _baseline = textView.DefaultBaseline;
        var gap = Math.Round(fontSize / 4);
        _left = hints[0].PaddingLeft ? gap : 0;
        _right = hints[^1].PaddingRight ? gap : 0;

        _text = new FormattedText(string.Join(" ", hints.Select(hint => hint.Label)), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(font), Math.Max(9, fontSize * 0.9), null);
        TextBlock.SetBaselineOffset(this, _baseline);
        ClipToBounds = false;   // drawn lower than its bounds on a line with a code lens (see LineBaseline)
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(_left + Padding + _text.Width + Padding + _right, _lineHeight);

    public bool HitTest(Point point) => false;

    public override void Render(DrawingContext context)
    {
        // Lined up with the code's baseline, wherever that is in the line.
        var top = LineBaseline() - _baseline;
        var background = new Rect(_left, top + 1, Padding + _text.Width + Padding, Math.Max(0, _lineHeight - 2));
        context.DrawRectangle(ThemeBrushes.Get("InlayHintBackgroundBrush"), null, background, 3, 3);

        _text.SetForegroundBrush(ThemeBrushes.Get("InlayHintForegroundBrush") ?? Brushes.Gray);
        context.DrawText(_text, new Point(_left + Padding, top + _baseline - _text.Baseline));
    }

    /// <summary>
    /// Where the code's baseline is, from this control's top. AvaloniaEdit puts inline objects at the top of their
    /// line, ignoring the baseline (TextView.ArrangeOverride), and a line with a code lens is taller with the code at
    /// the bottom: there the hint has to be drawn further down than its own bounds.
    /// </summary>
    private double LineBaseline()
    {
        var top = Bounds.Top + _textView.ScrollOffset.Y;   // in document coordinates, as visual lines count
        if (_textView.GetVisualLineFromVisualTop(top + 1) is { } visualLine)
            foreach (var textLine in visualLine.TextLines)
                if (Math.Abs(visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) - top) < 1)
                    return textLine.Baseline;
        return _baseline;
    }
}
