using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AXamlIDE.Views;

/// <summary>
/// Draws every text box's caret in place of Avalonia's own, which sits about a pixel inside the last letter at the
/// end of a line: TextPresenter.GetCaretPoints rounds the position down and then moves it a further pixel left there
/// (so it isn't clipped at the right edge), and Render is sealed, so it can't be changed. As the editor does with
/// CaretRenderer, Avalonia's caret is kept but given a transparent brush, and this draws the bar: in the gap after
/// the letter, with the same blink and the same rules for when it shows (focused, no selection).
///
/// It sits beside the TextPresenter in the template's panel, so it scrolls and clips with the text. A template without
/// that panel keeps Avalonia's caret. Turned on for the whole app by <see cref="Install"/> (App).
/// </summary>
public sealed class TextBoxCaret : Control
{
    private readonly TextBox _textBox;
    private readonly TextPresenter _presenter;
    private readonly DispatcherTimer _blink = new();
    private bool _on = true;

    private TextBoxCaret(TextBox textBox, TextPresenter presenter)
    {
        _textBox = textBox;
        _presenter = presenter;
        IsHitTestVisible = false;

        _blink.Tick += (_, _) =>
        {
            _on = !_on;
            InvalidateVisual();
        };
        // Typing, clicking and arrow keys move the caret: show it at once and restart the blink, as Avalonia does.
        presenter.CaretBoundsChanged += (_, _) => Restart();
        textBox.GotFocus += (_, _) => Restart();
        textBox.LostFocus += (_, _) =>
        {
            _blink.Stop();
            InvalidateVisual();
        };
        textBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty
                || e.Property == TextBox.CaretBrushProperty)
                InvalidateVisual();
        };
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                InvalidateVisual();   // the box was resized (scrolling moves this panel with it)
        };
    }

    /// <summary>Every text box from now on gets this caret (called once at startup).</summary>
    public static void Install()
        => TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((textBox, e) =>
        {
            if (e.NameScope.Find<TextPresenter>("PART_TextPresenter") is not { Parent: Panel panel } presenter)
                return;
            presenter.CaretBrush = Brushes.Transparent;   // Avalonia's caret: still there, but draws nothing
            panel.Children.Add(new TextBoxCaret(textBox, presenter));
        });

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_textBox.IsFocused)
            Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
    }

    public override void Render(DrawingContext context)
    {
        if (!_on || !_textBox.IsFocused || _textBox.SelectionStart != _textBox.SelectionEnd)
            return;
        var brush = _textBox.CaretBrush ?? _textBox.Foreground;
        if (brush is null)
            return;

        var layout = _presenter.TextLayout;
        var text = _presenter.Text ?? "";
        var index = Math.Clamp(_presenter.CaretIndex, 0, text.Length);
        var caret = layout.HitTestTextPosition(index);
        var atLineEnd = index == text.Length || text[index] is '\r' or '\n';

        // Where the presenter draws its text: it centres or bottom-aligns the layout when it's taller than the box.
        var top = 0.0;
        if (_presenter.Bounds.Height < layout.Height)
            top = _presenter.VerticalAlignment switch
            {
                VerticalAlignment.Center => (_presenter.Bounds.Height - layout.Height) / 2,
                VerticalAlignment.Bottom => _presenter.Bounds.Height - layout.Height,
                _ => 0
            };
        var origin = _presenter.Bounds.Position + new Vector(0, top);

        // One device pixel wide. At the end of a line, the first whole pixel at or after the caret's position: in the gap
        // after the last letter rather than over it. Between letters, the nearest pixel, so it touches neither side.
        // Kept inside the presenter, so a caret at the right edge isn't clipped away.
        var pixel = 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var column = (origin.X + caret.X) / pixel;
        var x = (atLineEnd ? Math.Ceiling(column) : Math.Round(column)) * pixel;
        x = Math.Min(x, _presenter.Bounds.Right - pixel);
        var y = Math.Floor((origin.Y + caret.Y) / pixel) * pixel;
        var bottom = Math.Ceiling((origin.Y + caret.Bottom) / pixel) * pixel;
        context.FillRectangle(brush, new Rect(x, y, pixel, bottom - y));
    }

    private void Restart()
    {
        _on = true;
        _blink.Stop();
        var interval = _textBox.CaretBlinkInterval;
        if (_textBox.IsFocused && interval > TimeSpan.Zero)
        {
            _blink.Interval = interval;
            _blink.Start();
        }
        InvalidateVisual();
    }
}
