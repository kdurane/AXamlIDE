using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// A vertical scrollbar for a <see cref="TextEditor"/> with the whole file mapped on it (VS Code's overview ruler):
/// the thumb is the part on screen (drag it; click elsewhere to jump there), the wheel over it scrolls the editor,
/// and subclasses draw their marks over it (<see cref="RenderMarks"/>). The editor's own vertical scrollbar should
/// be Hidden (not Disabled, so the wheel and keyboard still scroll it). Positions follow the editor's own layout
/// (folded and wrapped lines included). Used by <see cref="OverviewRuler"/> (editor) and Editor/Diff/DiffOverviewRuler.
/// </summary>
public abstract class EditorScrollRuler : Control
{
    protected const double MinMarkHeight = 3;
    private const int WheelLines = 3;

    private double? _grabOffset;   // while dragging the thumb: the pointer's distance below its top
    private bool _overThumb;

    protected TextEditor? Editor { get; private set; }

    /// <summary>Follow <paramref name="editor"/>: redraw as it scrolls or changes.</summary>
    protected void AttachEditor(TextEditor editor)
    {
        Editor = editor;
        var textView = editor.TextArea.TextView;
        textView.VisualLinesChanged += (_, _) => InvalidateVisual();   // scroll, edits, folding
        textView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public sealed override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // The editor's background (also makes the whole strip hit-testable).
        context.FillRectangle(ThemeBrushes.Get("AppWindowBackgroundBrush") ?? Brushes.Transparent, new Rect(size));
        if (ThemeBrushes.Get("OverviewRulerBorderBrush") is { } border)
            context.FillRectangle(border, new Rect(0, 0, 1, size.Height));

        if (Editor is not { } editor)
            return;
        var height = DocumentHeight(editor);
        if (height <= 0)
            return;

        var thumbBrush = _grabOffset is not null ? "OverviewRulerViewportActiveBrush"
            : _overThumb ? "OverviewRulerViewportHoverBrush"
            : "OverviewRulerViewportBrush";
        if (ThumbRect(editor) is { } thumb && ThemeBrushes.Get(thumbBrush) is { } viewport)
            context.FillRectangle(viewport, thumb);

        RenderMarks(context, editor.TextArea.TextView, size.Height / height);
    }

    /// <summary>Draw the marks over the thumb. <paramref name="scale"/>: ruler pixels per editor pixel.</summary>
    protected abstract void RenderMarks(DrawingContext context, TextView textView, double scale);

    /// <summary>The tooltip for the marks under <paramref name="y"/>, or null.</summary>
    protected virtual string? TipAt(double y) => null;

    /// <summary>Draw the caret line across the strip.</summary>
    protected void RenderCaretLine(DrawingContext context, TextView textView, int line, double scale)
    {
        if (ThemeBrushes.Get("OverviewRulerCaretBrush") is not { } caret)
            return;
        var y = LineTop(textView, line) * scale;
        context.FillRectangle(caret, new Rect(1, Math.Min(y, Bounds.Height - 2), Bounds.Width - 1, 2));
    }

    // ---------------------------------------------------------------- scrolling

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Editor is not { } editor || ThumbRect(editor) is not { } thumb)
            return;

        // Grab the thumb where it was pressed; pressed elsewhere, it jumps so its middle is under the pointer.
        var y = e.GetPosition(this).Y;
        _grabOffset = y >= thumb.Top && y <= thumb.Bottom ? y - thumb.Top : thumb.Height / 2;
        e.Pointer.Capture(this);
        MoveThumbTo(y - _grabOffset.Value);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var y = e.GetPosition(this).Y;
        if (_grabOffset is { } grab)
        {
            MoveThumbTo(y - grab);
            return;
        }

        var overThumb = Editor is { } editor && ThumbRect(editor) is { } thumb && y >= thumb.Top && y <= thumb.Bottom;
        if (overThumb != _overThumb)
        {
            _overThumb = overThumb;
            InvalidateVisual();
        }
        ToolTip.SetTip(this, TipAt(y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_overThumb)
            return;
        _overThumb = false;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    /// <summary>The wheel over the scrollbar scrolls the editor, as over any scrollbar.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Editor is not { } editor)
            return;

        var step = editor.TextArea.TextView.DefaultLineHeight * WheelLines;
        ScrollEditorTo(editor, editor.VerticalOffset - e.Delta.Y * step);
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_grabOffset is null)
            return;
        _grabOffset = null;
        InvalidateVisual();
    }

    /// <summary>Scroll so the thumb's top is at <paramref name="thumbTop"/>.</summary>
    private void MoveThumbTo(double thumbTop)
    {
        if (Editor is not { } editor)
            return;
        var height = DocumentHeight(editor);
        if (height > 0)
            ScrollEditorTo(editor, thumbTop / Bounds.Height * height);
    }

    private static void ScrollEditorTo(TextEditor editor, double offset)
    {
        var maximum = Math.Max(0, DocumentHeight(editor) - editor.TextArea.TextView.Bounds.Height);
        editor.ScrollToOffset(editor.HorizontalOffset, Math.Clamp(offset, 0, maximum));
    }

    /// <summary>Where the thumb is, or null when the whole file fits on screen (nothing to scroll).</summary>
    private Rect? ThumbRect(TextEditor editor)
    {
        var height = DocumentHeight(editor);
        var textView = editor.TextArea.TextView;
        if (height <= 0 || textView.Bounds.Height >= height)
            return null;

        var scale = Bounds.Height / height;
        return new Rect(1, textView.VerticalOffset * scale, Bounds.Width - 1, Math.Max(MinMarkHeight * 4, textView.Bounds.Height * scale));
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The ruler's scale (ruler pixels per editor pixel), or 0 when there's nothing to map.</summary>
    protected double Scale()
    {
        var height = Editor is { } editor ? DocumentHeight(editor) : 0;
        return height > 0 ? Bounds.Height / height : 0;
    }

    /// <summary>The height of the whole file as the editor lays it out (folded/wrapped lines included).</summary>
    private static double DocumentHeight(TextEditor editor)
    {
        var textView = editor.TextArea.TextView;
        return textView.Document is null ? 0 : Math.Max(textView.DocumentHeight, textView.Bounds.Height);
    }

    protected static double LineTop(TextView textView, int line)
        => textView.Document is { } document
            ? textView.GetVisualTopByDocumentLine(Math.Clamp(line, 1, document.LineCount))
            : 0;

    /// <summary>The mark for document lines <paramref name="firstLine"/> to <paramref name="lastLine"/> (1-based).</summary>
    protected static Rect LinesRect(TextView textView, int firstLine, int lastLine, double x, double width, double scale)
    {
        var top = LineTop(textView, firstLine) * scale;
        var bottom = (LineTop(textView, lastLine) + textView.DefaultLineHeight) * scale;
        return new Rect(x, top, width, Math.Max(MinMarkHeight, bottom - top));
    }
}
