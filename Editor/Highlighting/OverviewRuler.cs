using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.GitChanges;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// The editor's vertical scrollbar, with the whole file mapped on it (VS Code's overview ruler):
///
///   ▒   the thumb: the part of the file on screen (drag it; click elsewhere to jump there)
///   ─   the caret line (full width)
///   ▏   lines changed since the last commit (a thin lane at the left, as in VS Code: Editor/GitChanges)
///   ▌   other occurrences of the selected text, and the find panel's matches while it's open (left half of the rest)
///    ▐  errors, warnings and info (right half of the rest; errors drawn last, so on top)
///
/// Scrolling is <see cref="EditorScrollRuler"/>'s; hover a diagnostic mark for its message. Declared in
/// EditorPaneView.axaml, given the editor by <see cref="Attach"/>. Colours: the OverviewRuler* brushes
/// (editorOverviewRuler.* and scrollbarSlider.* theme keys).
/// </summary>
public sealed class OverviewRuler : EditorScrollRuler
{
    private const double MarkHitSlop = 2;
    private const double GitLaneWidth = 3;

    private EditorContext? _ctx;
    private OccurrenceController? _occurrences;
    private GitChangeController? _gitChanges;
    private FindMarksController? _findMarks;

    public void Attach(EditorContext ctx, OccurrenceController occurrences, GitChangeController gitChanges, FindMarksController findMarks)
    {
        _findMarks = findMarks;
        findMarks.Changed += InvalidateVisual;
        _ctx = ctx;
        _occurrences = occurrences;
        _gitChanges = gitChanges;
        AttachEditor(ctx.Editor);
        gitChanges.Changed += InvalidateVisual;
        ctx.Editor.TextArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
        occurrences.Changed += InvalidateVisual;
        ctx.Attached += _ => InvalidateVisual();
    }

    /// <summary>The document's diagnostics changed (EditorPaneView forwards DiagnosticsChangedMessage).</summary>
    public void Refresh() => InvalidateVisual();

    protected override void RenderMarks(DrawingContext context, TextView textView, double scale)
    {
        if (_ctx is not { Active: { } document } ctx)
            return;
        var left = 1 + GitLaneWidth;
        var half = (Bounds.Width - left) / 2;

        // Git changes: the thin lane at the left.
        if (_gitChanges is { Changes.Count: > 0 } git)
            foreach (var change in git.Changes)
                if (GitBrush(change.Kind) is { } brush)
                    context.FillRectangle(brush, change.Kind == LineChangeKind.Deleted
                        ? LinesRect(textView, Math.Max(1, change.FirstLine), Math.Max(1, change.FirstLine), 1, GitLaneWidth, scale)
                        : LinesRect(textView, change.FirstLine, change.LastLine, 1, GitLaneWidth, scale));

        // Occurrences: left half of the rest.
        if (_occurrences is { Segments.Count: > 0 } && ThemeBrushes.Get("OverviewRulerOccurrenceBrush") is { } occurrence)
            foreach (var segment in _occurrences.Segments)
                context.FillRectangle(occurrence, MarkRect(textView, document.Document, segment.StartOffset, left, half, scale));

        // Find matches: the same lane, over the occurrences (VS Code puts both in its middle lane).
        if (_findMarks is { Offsets.Count: > 0 } find && ThemeBrushes.Get("OverviewRulerFindMatchBrush") is { } match)
            foreach (var offset in find.Offsets)
                context.FillRectangle(match, MarkRect(textView, document.Document, offset, left, half, scale));

        // Diagnostics: right half, least severe first so errors end up on top.
        // Faded-only code (an unused using) gets no mark, as in Visual Studio: it isn't a problem to go looking for.
        foreach (var diagnostic in document.Diagnostics.Where(d => d.IsSquiggled).OrderByDescending(d => d.Severity))
            if (MarkBrush(diagnostic.Severity) is { } brush)
                context.FillRectangle(brush, MarkRect(textView, document.Document, diagnostic.StartOffset, left + half, half, scale));

        RenderCaretLine(context, textView, ctx.Editor.TextArea.Caret.Line, scale);
    }

    /// <summary>The messages of the diagnostics marked under <paramref name="y"/>, or null.</summary>
    protected override string? TipAt(double y)
    {
        var scale = Scale();
        if (_ctx is not { Active: { } document } ctx || scale <= 0)
            return null;

        var textView = ctx.Editor.TextArea.TextView;
        var messages = document.Diagnostics
            .Where(d => d.IsSquiggled)
            .Where(d => MarkRect(textView, document.Document, d.StartOffset, 0, 1, scale).Inflate(new Thickness(0, MarkHitSlop)) is var r
                        && y >= r.Top && y <= r.Bottom)
            .OrderBy(d => d.Severity)
            .Select(d => $"{document.Document.GetLineByOffset(d.StartOffset).LineNumber}: {d.Message}")
            .Distinct()
            .Take(8)
            .ToList();
        return messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    private static Rect MarkRect(TextView textView, TextDocument document, int offset, double x, double width, double scale)
    {
        var line = document.GetLineByOffset(Math.Clamp(offset, 0, document.TextLength)).LineNumber;
        return LinesRect(textView, line, line, x, width, scale);
    }

    private static IBrush? GitBrush(LineChangeKind kind) => ThemeBrushes.Get(kind switch
    {
        LineChangeKind.Added => "OverviewRulerAddedBrush",
        LineChangeKind.Modified => "OverviewRulerModifiedBrush",
        _ => "OverviewRulerDeletedBrush"
    });

    private static IBrush? MarkBrush(int severity) => ThemeBrushes.Get(severity switch
    {
        LspDiagnosticSeverity.Error => "OverviewRulerErrorBrush",
        LspDiagnosticSeverity.Warning => "OverviewRulerWarningBrush",
        _ => "OverviewRulerInfoBrush"
    });
}
