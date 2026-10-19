using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Editor.Diff;

/// <summary>
/// Colours whole lines of one side of a diff: removed lines on the left, added lines on the right
/// (a changed line counts as both), and filler lines. The brushes are app resources (Styles/ThemeResources.axaml),
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
            var top = line.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, line.Height));
        }
    }

    private IBrush? BrushFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Removed => Resource("DiffRemovedLineBrush"),
        DiffLineKind.Added => Resource("DiffInsertedLineBrush"),
        DiffLineKind.Modified => Resource(isLeftSide ? "DiffRemovedLineBrush" : "DiffInsertedLineBrush"),
        DiffLineKind.Filler => Resource("DiffFillerLineBrush"),
        _ => null
    };

    private static IBrush? Resource(string key)
        => Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true ? brush as IBrush : null;
}
