using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AXamlIDE.Views;

public sealed class PreviewGridLines : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var gridPen = new Pen(ThemeBrushes.Get("PreviewGridLineBrush"));
        var majorGridPen = new Pen(ThemeBrushes.Get("PreviewGridLineMajorBrush"));

        var horizontalLineCount = (int)(Bounds.Height / 10);
        for (var index = 1; index <= horizontalLineCount; index++)
        {
            var y = index * 10;
            context.DrawLine(index % 10 == 0 ? majorGridPen : gridPen,
                new Point(0, y), new Point(Bounds.Width, y));
        }

        var verticalLineCount = (int)(Bounds.Width / 10);
        for (var index = 1; index <= verticalLineCount; index++)
        {
            var x = index * 10;
            context.DrawLine(index % 10 == 0 ? majorGridPen : gridPen,
                new Point(x, 0), new Point(x, Bounds.Height));
        }
    }
}
