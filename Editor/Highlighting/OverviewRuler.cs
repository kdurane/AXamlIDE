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
/// The strip beside the editor's vertical scrollbar that maps the whole file (VS Code's overview ruler):
///
///   ▒   the part of the file on screen
///   ─   the caret line (full width)
///   ▌   other occurrences of the selected text (left half)
///    ▐  errors, warnings and info (right half; errors drawn last, so on top)
///
/// Click or drag to scroll there; hover a diagnostic mark for its message. Positions follow the editor's own
/// layout (folded and wrapped lines included). Declared in EditorPaneView.axaml, given the editor by
/// <see cref="Attach"/>. Colours are the OverviewRuler* brushes (editorOverviewRuler.* theme keys).
/// </summary>
public sealed class OverviewRuler : Control
{
    private const double MinMarkHeight = 3;
    private const double MarkHitSlop = 2;

    private EditorContext? _ctx;
    private OccurrenceController? _occurrences;
    private bool _dragging;

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

        // The part on screen.
        if (ThemeBrushes.Get("OverviewRulerViewportBrush") is { } viewport && textView.Bounds.Height < height)
            context.FillRectangle(viewport, new Rect(1, textView.VerticalOffset * scale, size.Width - 1,
                Math.Max(MinMarkHeight, textView.Bounds.Height * scale)));

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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _ctx is null)
            return;

        _dragging = true;
        e.Pointer.Capture(this);
        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var y = e.GetPosition(this).Y;
        if (_dragging)
            ScrollTo(y);
        else
            ToolTip.SetTip(this, DiagnosticsTextAt(y));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>Scroll so the point of the file under <paramref name="y"/> is in the middle of the editor.</summary>
    private void ScrollTo(double y)
    {
        if (_ctx is not { } ctx)
            return;
        var height = DocumentHeight(ctx);
        if (height <= 0)
            return;

        var textView = ctx.Editor.TextArea.TextView;
        var target = y / Bounds.Height * height - textView.Bounds.Height / 2;
        ctx.Editor.ScrollToOffset(ctx.Editor.HorizontalOffset, Math.Clamp(target, 0, Math.Max(0, height - textView.Bounds.Height)));
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
