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

        // Re-apply once mapped, in case the window manager moved it.
        Opened += (_, _) => Position = _initialPosition ?? Position;
    }

    private PixelPoint? _initialPosition;

    /// <summary>
    /// Show owned by <paramref name="owner"/>, where the user last left it or centred near its top.
    /// The position is set BEFORE showing: on X11, a move made after the window is mapped can be
    /// ignored by the window manager, which then leaves it at the top-left.
    /// </summary>
    public void ShowOver(Window owner)
    {
        _initialPosition = _savedPosition ?? CentredNearTopOf(owner);
        Position = _initialPosition.Value;
        Show(owner);
    }

    private PixelPoint CentredNearTopOf(Window owner)
    {
        var scale = owner.RenderScaling;
        var width = double.IsNaN(Width) ? Bounds.Width : Width;
        var x = owner.Position.X + (int)((owner.Bounds.Width - width) * scale / 2);
        var y = owner.Position.Y + (int)(80 * scale);
        return new PixelPoint(x, y);
    }
}
