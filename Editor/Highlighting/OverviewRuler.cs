using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// The editor's vertical scrollbar, with the whole file mapped on it (VS Code's overview ruler):
///
///   ▒   the thumb: the part of the file on screen (drag it; click elsewhere to jump there)
///   ─   the caret line (full width)
///   ▌   other occurrences of the selected text (left half)
///    ▐  errors, warnings and info (right half; errors drawn last, so on top)
///
/// The editor's own vertical scrollbar is hidden (Hidden, not Disabled, so the wheel and keyboard still
/// scroll it). The wheel over this strip scrolls the editor too; hover a diagnostic mark for its message.
/// Positions follow the editor's own layout (folded and wrapped lines included). Declared in
/// EditorPaneView.axaml, given the editor by <see cref="Attach"/>. Colours: the OverviewRuler* brushes
/// (editorOverviewRuler.* and scrollbarSlider.* theme keys).
/// </summary>
public sealed class OverviewRuler : Control
{
    private const double MinMarkHeight = 3;
    private const double MarkHitSlop = 2;
    private const int WheelLines = 3;

    private EditorContext? _ctx;
    private OccurrenceController? _occurrences;
    private double? _grabOffset;   // while dragging the thumb: the pointer's distance below its top
    private bool _overThumb;

    public void Attach(EditorContext ctx, OccurrenceController occurrences)
    {
        _ctx = ctx;
        _occurrences = occurrences;

        var textView = ctx.Editor.TextArea.TextView;
        textView.VisualLinesChanged += (_, _) => InvalidateVisual();   // scroll, edits, folding
        textView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        ctx.Editor.TextArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
        occurrences.Changed += InvalidateVisual;
        ctx.Attached += _ => InvalidateVisual();
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>The document's diagnostics changed (EditorPaneView forwards DiagnosticsChangedMessage).</summary>
    public void Refresh() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // The editor's background (also makes the whole strip hit-testable).
        context.FillRectangle(ThemeBrushes.Get("AppWindowBackgroundBrush") ?? Brushes.Transparent, new Rect(size));
        if (ThemeBrushes.Get("OverviewRulerBorderBrush") is { } border)
            context.FillRectangle(border, new Rect(0, 0, 1, size.Height));

        if (_ctx is not { Active: { } document } ctx)
            return;
        var height = DocumentHeight(ctx);
        if (height <= 0)
            return;

        var textView = ctx.Editor.TextArea.TextView;
        var scale = size.Height / height;

        // The thumb: the part on screen.
        var thumbBrush = _grabOffset is not null ? "OverviewRulerViewportActiveBrush"
            : _overThumb ? "OverviewRulerViewportHoverBrush"
            : "OverviewRulerViewportBrush";
        if (ThumbRect(ctx) is { } thumb && ThemeBrushes.Get(thumbBrush) is { } viewport)
            context.FillRectangle(viewport, thumb);

        var half = (size.Width - 1) / 2;

        // Occurrences: left half.
        if (_occurrences is { Segments.Count: > 0 } && ThemeBrushes.Get("OverviewRulerOccurrenceBrush") is { } occurrence)
            foreach (var segment in _occurrences.Segments)
                context.FillRectangle(occurrence, MarkRect(textView, document.Document, segment.StartOffset, 1, half, scale));

        // Diagnostics: right half, least severe first so errors end up on top.
        foreach (var diagnostic in document.Diagnostics.OrderByDescending(d => d.Severity))
            if (MarkBrush(diagnostic.Severity) is { } brush)
                context.FillRectangle(brush, MarkRect(textView, document.Document, diagnostic.StartOffset, 1 + half, half, scale));

        // The caret line, full width.
        if (ThemeBrushes.Get("OverviewRulerCaretBrush") is { } caret)
        {
            var y = LineTop(textView, ctx.Editor.TextArea.Caret.Line) * scale;
            context.FillRectangle(caret, new Rect(1, Math.Min(y, size.Height - 2), size.Width - 1, 2));
        }
    }

    // ---------------------------------------------------------------- scrolling

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _ctx is null || ThumbRect(_ctx) is not { } thumb)
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

        var overThumb = _ctx is not null && ThumbRect(_ctx) is { } thumb && y >= thumb.Top && y <= thumb.Bottom;
        if (overThumb != _overThumb)
        {
            _overThumb = overThumb;
            InvalidateVisual();
        }
        ToolTip.SetTip(this, DiagnosticsTextAt(y));
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
        if (_ctx is not { } ctx)
            return;

        var editor = ctx.Editor;
        var step = editor.TextArea.TextView.DefaultLineHeight * WheelLines;
        ScrollEditorTo(ctx, editor.VerticalOffset - e.Delta.Y * step);
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
        if (_ctx is not { } ctx)
            return;
        var height = DocumentHeight(ctx);
        if (height > 0)
            ScrollEditorTo(ctx, thumbTop / Bounds.Height * height);
    }

    private static void ScrollEditorTo(EditorContext ctx, double offset)
    {
        var maximum = Math.Max(0, DocumentHeight(ctx) - ctx.Editor.TextArea.TextView.Bounds.Height);
        ctx.Editor.ScrollToOffset(ctx.Editor.HorizontalOffset, Math.Clamp(offset, 0, maximum));
    }

    /// <summary>Where the thumb is, or null when the whole file fits on screen (nothing to scroll).</summary>
    private Rect? ThumbRect(EditorContext ctx)
    {
        var height = DocumentHeight(ctx);
        var textView = ctx.Editor.TextArea.TextView;
        if (height <= 0 || textView.Bounds.Height >= height)
            return null;

        var scale = Bounds.Height / height;
        return new Rect(1, textView.VerticalOffset * scale, Bounds.Width - 1, Math.Max(MinMarkHeight * 4, textView.Bounds.Height * scale));
    }

    /// <summary>The messages of the diagnostics marked under <paramref name="y"/>, or null.</summary>
    private string? DiagnosticsTextAt(double y)
    {
        if (_ctx is not { Active: { } document } ctx)
            return null;
        var height = DocumentHeight(ctx);
        if (height <= 0)
            return null;

        var textView = ctx.Editor.TextArea.TextView;
        var scale = Bounds.Height / height;
        var messages = document.Diagnostics
            .Where(d => MarkRect(textView, document.Document, d.StartOffset, 0, 1, scale).Inflate(new Thickness(0, MarkHitSlop)) is var r
                        && y >= r.Top && y <= r.Bottom)
            .OrderBy(d => d.Severity)
            .Select(d => $"{document.Document.GetLineByOffset(d.StartOffset).LineNumber}: {d.Message}")
            .Distinct()
            .Take(8)
            .ToList();
        return messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The height of the whole file as the editor lays it out (folded/wrapped lines included).</summary>
    private static double DocumentHeight(EditorContext ctx)
    {
        var textView = ctx.Editor.TextArea.TextView;
        return textView.Document is null ? 0 : Math.Max(textView.DocumentHeight, textView.Bounds.Height);
    }

    private static double LineTop(TextView textView, int line)
        => textView.Document is { } document
            ? textView.GetVisualTopByDocumentLine(Math.Clamp(line, 1, document.LineCount))
            : 0;

    private static Rect MarkRect(TextView textView, TextDocument document, int offset, double x, double width, double scale)
    {
        var line = document.GetLineByOffset(Math.Clamp(offset, 0, document.TextLength));
        var top = LineTop(textView, line.LineNumber) * scale;
        var lineHeight = textView.DefaultLineHeight * scale;
        return new Rect(x, top, width, Math.Max(MinMarkHeight, lineHeight));
    }

    private static IBrush? MarkBrush(int severity) => ThemeBrushes.Get(severity switch
    {
        LspDiagnosticSeverity.Error => "OverviewRulerErrorBrush",
        LspDiagnosticSeverity.Warning => "OverviewRulerWarningBrush",
        _ => "OverviewRulerInfoBrush"
    });
}
