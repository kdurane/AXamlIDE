using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AXamlIDE.Views;

public partial class DebugToolbar : UserControl
{
    private readonly TranslateTransform _move = new();
    private Point? _last;

    public DebugToolbar()
    {
        InitializeComponent();
        Root.RenderTransform = _move;
        Grip.PointerPressed += (_, e) =>
        {
            _last = e.GetPosition((Visual?)Parent);
            e.Pointer.Capture(Grip);
        };
        Grip.PointerMoved += (_, e) =>
        {
            if (_last is not { } last)
                return;
            var p = e.GetPosition((Visual?)Parent);
            _move.X += p.X - last.X;
            _move.Y += p.Y - last.Y;
            _last = p;
        };
        Grip.PointerReleased += (_, e) =>
        {
            _last = null;
            e.Pointer.Capture(null);
        };
    }
}
