using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using AvaloniaEdit.Rendering;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.CodeLens;

/// <summary>
/// One code lens ("3 references") as an inline object in the declaration's line. It has no width (the code
/// doesn't move) and is one lens row taller than the line, with its baseline at the bottom: the code stays on
/// its own row and the lens is drawn in the space above, from the declaration's indentation. Only the text is
/// clickable (<see cref="ICustomHitTest"/>), so a click anywhere else still puts the caret there.
/// </summary>
internal sealed class CodeLensControl : Control, ICustomHitTest
{
    private readonly TextView _textView;
    private readonly Action<CodeLensControl> _clicked;
    private readonly FormattedText _text;
    private readonly double _lensHeight;
    private bool _hovered;

    public CodeLensControl(CodeLensItem item, TextView textView, FontFamily font, double fontSize, Action<CodeLensControl> clicked)
    {
        Item = item;
        _textView = textView;
        _clicked = clicked;
        _lensHeight = Math.Ceiling(textView.DefaultLineHeight * 0.95);

        // A little smaller than the code, in the editor's font, as VS Code does.
        _text = new FormattedText(item.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font), Math.Max(9, fontSize * 0.85), null);

        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Show references");
        TextBlock.SetBaselineOffset(this, _lensHeight + textView.DefaultBaseline);
    }

    public CodeLensItem Item { get; }

    /// <summary>The band above the code this lens is drawn in.</summary>
    public double LensHeight => _lensHeight;

    /// <summary>Where the text is, for placing the references list under it.</summary>
    public Rect TextBounds => new(0, 0, _text.Width, _lensHeight);

    protected override Size MeasureOverride(Size availableSize) => new(0, _lensHeight + _textView.DefaultLineHeight);

    public bool HitTest(Point point) => TextBounds.Contains(point);

    public override void Render(DrawingContext context)
    {
        _text.SetForegroundBrush(ThemeBrushes.Get(_hovered ? "LinkBrush" : "CodeLensForegroundBrush") ?? Brushes.Gray);
        _text.SetTextDecorations(_hovered ? TextDecorations.Underline : []);
        context.DrawText(_text, new Point(0, Math.Max(0, (_lensHeight - _text.Height) / 2)));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        InvalidateVisual();
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
