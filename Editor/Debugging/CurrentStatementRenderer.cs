using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Debugging;

/// <summary>
/// Highlights the paused statement's line, as Visual Studio does: yellow for the current statement, green when
/// another frame is picked in the Call Stack (theme keys editor.stackFrameHighlightBackground and
/// editor.focusedStackFrameHighlightBackground). Set <see cref="Statement"/> and redraw the background layer.
/// </summary>
public sealed class CurrentStatementRenderer : IBackgroundRenderer
{
    /// <summary>The line (1-based) and whether it's the top frame's; null for none.</summary>
    public (int Line, bool IsTop)? Statement { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Statement is not { } statement || !textView.VisualLinesValid || textView.Document is null
            || statement.Line < 1 || statement.Line > textView.Document.LineCount)
            return;

        var brush = ThemeBrushes.Get(statement.IsTop ? "StackFrameHighlightBrush" : "FocusedStackFrameHighlightBrush");
        if (brush is null)
            return;

        var line = textView.Document.GetLineByNumber(statement.Line);
        foreach (var visualLine in textView.VisualLines)
        {
            if (visualLine.FirstDocumentLine.LineNumber > line.LineNumber || visualLine.LastDocumentLine.LineNumber < line.LineNumber)
                continue;
            var top = visualLine.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, visualLine.Height));
        }
    }
}
