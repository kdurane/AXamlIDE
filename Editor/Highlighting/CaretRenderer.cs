using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Utils;
using AXamlIDE.Editor.CodeLens;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Draws the caret in place of AvaloniaEdit's own (its layer is internal, so it is kept but given a transparent
/// brush): the same bar, but not over a code lens's band above its line (<see cref="CodeLensSpace"/>), as the
/// selection does. AvaloniaEdit sizes the caret from the line's top to its bottom (LineTop/LineBottom, since its
/// PR #546), and a lens makes its line taller. Drawn as AvaloniaEdit draws it: in the text colour, one device pixel wide.
/// </summary>
public sealed class CaretRenderer : IBackgroundRenderer
{
    private readonly TextArea _textArea;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _on = true;

    public CaretRenderer(TextArea textArea)
    {
        _textArea = textArea;
        _blink.Tick += (_, _) =>
        {
            _on = !_on;
            Redraw();
        };
        // Moving the caret (typing, clicking, arrow keys) shows it at once and restarts the blink, as every editor does.
        textArea.Caret.PositionChanged += (_, _) => Restart();
        textArea.GotFocus += (_, _) => Restart();
        textArea.LostFocus += (_, _) =>
        {
            _blink.Stop();
            Redraw();
        };
    }

    public KnownLayer Layer => KnownLayer.Caret;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!_on || !_textArea.IsFocused || !textView.VisualLinesValid || textView.GetValue(TextBlock.ForegroundProperty) is not { } brush)
            return;
        // Only when the caret's line is already built (on screen). Otherwise CalculateCaretRectangle builds it, and
        // building a line with a code lens, inlay hint or colour swatch adds a control mid-render, which Avalonia
        // refuses ("Visual was invalidated during the render pass"). A caret that's off screen isn't seen anyway.
        if (textView.GetVisualLine(_textArea.Caret.Line) is null)
            return;
        // The caret's rectangle is in document coordinates; CodeLensSpace.Trim works in the text view's. It's only
        // 0.5 wide: snapped to whole device pixels (as AvaloniaEdit's caret layer does), that's one solid pixel
        // instead of a faint, blended half.
        var rect = _textArea.Caret.CalculateCaretRectangle().Translate(-textView.ScrollOffset);
        drawingContext.FillRectangle(brush, PixelSnapHelpers.Round(CodeLensSpace.Trim(textView, rect), PixelSnapHelpers.GetPixelSize(textView)));
    }

    private void Restart()
    {
        _on = true;
        _blink.Stop();
        if (_textArea.IsFocused)
            _blink.Start();
        Redraw();
    }

    private void Redraw() => _textArea.TextView.InvalidateLayer(Layer);
}
