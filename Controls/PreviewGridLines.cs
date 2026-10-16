using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AXamlIDE.Controls
{
    public sealed class PreviewGridLines : Control
    {
        private static readonly Pen GridPen = new(
            new SolidColorBrush(Color.FromArgb(25, 14, 94, 253)),
            1);
        private static readonly Pen MajorGridPen = new(
            new SolidColorBrush(Color.FromArgb(77, 14, 94, 253)),
            1);

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var horizontalLineCount = (int)(Bounds.Height / 10);
            for (var index = 1; index <= horizontalLineCount; index++)
            {
                var y = index * 10;
                context.DrawLine(index % 10 == 0 ? MajorGridPen : GridPen,
                    new Point(0, y), new Point(Bounds.Width, y));
            }

            var verticalLineCount = (int)(Bounds.Width / 10);
            for (var index = 1; index <= verticalLineCount; index++)
            {
                var x = index * 10;
                context.DrawLine(index % 10 == 0 ? MajorGridPen : GridPen,
                    new Point(x, 0), new Point(x, Bounds.Height));
            }
        }
    }
}
