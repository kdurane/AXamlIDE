using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AXamlIDE.Views;

public partial class DebugToolbarWindow : Window
{
    private readonly PixelPoint? _savedPosition;

    public DebugToolbarWindow() : this(null)
    {
    }

    /// <param name="savedPosition">Where the user last left it; null = centre near the top of the owner.</param>
    public DebugToolbarWindow(PixelPoint? savedPosition)
    {
        _savedPosition = savedPosition;
        InitializeComponent();

        // Buttons handle their own presses, so this only fires on the empty parts of the border.
        Root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        Opened += (_, _) => Place();
    }

    private void Place()
    {
        if (_savedPosition is { } saved)
        {
            Position = saved;
            return;
        }

        if (Owner is not Window owner)
            return;

        var scale = owner.RenderScaling;
        var x = owner.Position.X + (int)((owner.Bounds.Width - Bounds.Width) * scale / 2);
        var y = owner.Position.Y + (int)(80 * scale);
        Position = new PixelPoint(x, y);
    }
}
