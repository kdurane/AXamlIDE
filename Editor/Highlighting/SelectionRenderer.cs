using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.CodeLens;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Draws the selection in place of AvaloniaEdit's own selection layer (which is internal, so it is kept but given a
/// transparent brush): the same shape, but not over a code lens's band above its line (<see cref="CodeLensSpace"/>).
/// TextMateController sets the brush from the theme.
/// </summary>
public sealed class SelectionRenderer(TextArea textArea) : IBackgroundRenderer
{
    public IBrush? Brush
    {
        get;
        set
        {
            field = value;
            textArea.TextView.InvalidateLayer(Layer);   // a theme change: redraw with the new colour
        }
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Brush is null || !textView.VisualLinesValid)
            return;

        // As AvaloniaEdit's SelectionLayer does, but each rectangle trimmed before it's joined into the shape.
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            BorderThickness = textArea.SelectionBorder?.Thickness ?? 0,
            ExtendToFullWidthAtLineEnd = textArea.Selection.EnableVirtualSpace,
            CornerRadius = textArea.SelectionCornerRadius
        };
        foreach (var segment in textArea.Selection.Segments)
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, builder.ExtendToFullWidthAtLineEnd))
            builder.AddRectangle(textView, CodeLensSpace.Trim(textView, rect));

        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(Brush, textArea.SelectionBorder, geometry);
    }
}
