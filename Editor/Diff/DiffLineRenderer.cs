using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.CodeLens;
using AXamlIDE.Services.Git;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Diff;

/// <summary>
/// Colours whole lines of one side of a diff: removed lines on the left, added lines on the right
/// (a changed line counts as both), and filler lines; in a changed line, the words that changed get a stronger
/// colour over that (as VS Code and Rider do). The brushes are app resources (Styles/ThemeResources.axaml),
/// so they follow the theme.
/// </summary>
public sealed class DiffLineRenderer(bool isLeftSide) : IBackgroundRenderer
{
    /// <summary>One entry per document line.</summary>
    public IReadOnlyList<DiffLine> Lines { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
            return;

        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if (index >= Lines.Count || BrushFor(Lines[index].Kind) is not { } brush)
                continue;
            var lens = CodeLensSpace.Above(line);
            var top = line.VisualTop - textView.VerticalOffset + lens;
            drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, line.Height - lens));
            if (Lines[index].ChangedSpans is { Count: > 0 } spans)
                DrawWords(textView, drawingContext, line, spans);
        }
    }

    private void DrawWords(TextView textView, DrawingContext drawingContext, VisualLine line, IReadOnlyList<DiffSpan> spans)
    {
        if (ThemeBrushes.Get(isLeftSide ? "DiffRemovedTextBrush" : "DiffInsertedTextBrush") is not { } brush)
            return;
        var documentLine = line.FirstDocumentLine;
        foreach (var span in spans)
        {
            var start = documentLine.Offset + Math.Min(span.Start, documentLine.Length);
            var end = documentLine.Offset + Math.Min(span.Start + span.Length, documentLine.Length);
            if (end <= start)
                continue;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, EndOffset = end }))
                drawingContext.FillRectangle(brush, rect);
        }
    }

    private IBrush? BrushFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Removed => ThemeBrushes.Get("DiffRemovedLineBrush"),
        DiffLineKind.Added => ThemeBrushes.Get("DiffInsertedLineBrush"),
        DiffLineKind.Modified => ThemeBrushes.Get(isLeftSide ? "DiffRemovedLineBrush" : "DiffInsertedLineBrush"),
        DiffLineKind.Filler => ThemeBrushes.Get("DiffFillerLineBrush"),
        _ => null
    };
}
