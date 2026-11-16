using System;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.CodeLens;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Draws the caret in place of AvaloniaEdit's own (its layer is internal, so it is kept but given a transparent
/// brush): the same bar, but not over a code lens's band above its line (<see cref="CodeLensSpace"/>), as the
/// selection does. AvaloniaEdit sizes the caret from the line's top to its bottom (LineTop/LineBottom, since its
/// PR #546), and a lens makes its line taller. TextMateController sets the brush from the theme.
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

    /// <summary>The theme's "editorCursor.foreground"; null draws in the editor's text colour.</summary>
    public IBrush? Brush
    {
        get;
        set
        {
            field = value;
            Redraw();   // a theme change: redraw with the new colour
        }
    }

    public KnownLayer Layer => KnownLayer.Caret;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!_on || !_textArea.IsFocused || !textView.VisualLinesValid || (Brush ?? _textArea.Foreground) is not { } brush)
            return;
        // The caret's rectangle is in document coordinates; CodeLensSpace.Trim works in the text view's.
        var rect = _textArea.Caret.CalculateCaretRectangle().Translate(-textView.ScrollOffset);
        drawingContext.FillRectangle(brush, CodeLensSpace.Trim(textView, rect));
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
