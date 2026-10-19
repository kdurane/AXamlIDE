using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Models;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// One renderer for the one shared editor. On tab switch, point <see cref="Segments"/>
/// at the active document's collection (null when nothing is open).
/// </summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    public TextSegmentCollection<DiagnosticSegment>? Segments { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Segments is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;

        var viewStart = textView.VisualLines.First().FirstDocumentLine.Offset;
        var viewEnd = textView.VisualLines.Last().LastDocumentLine.EndOffset;

        foreach (var seg in Segments.FindOverlappingSegments(viewStart, viewEnd - viewStart))
        {
            // The theme's error / warning / info colour (the same as in the hover card).
            var pen = new Pen(HoverCard.DiagnosticBrush(seg.Severity) ?? Brushes.Red);

            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                DrawSquiggle(dc, pen, r.BottomLeft, r.Width);
        }
    }

    private static void DrawSquiggle(DrawingContext dc, IPen pen, Point start, double width)
    {
        const double step = 2.5, amp = 1.5;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(start.X, start.Y - amp), false);
            var up = false;
            for (var x = start.X + step; x < start.X + width + step; x += step)
            {
                ctx.LineTo(new Point(x, start.Y - (up ? amp : 0)));
                up = !up;
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geo);
    }
}
