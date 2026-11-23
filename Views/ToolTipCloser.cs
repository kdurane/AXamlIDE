using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace AXamlIDE.Views;

/// <summary>
/// Closes every open tooltip when its window stops being the one in use: when it's deactivated (another app comes in
/// front) and when the pointer leaves it. On Linux a tooltip is its own always-on-top popup window, and Avalonia only
/// closes it when it sees the pointer leave the control; leave the window quickly, or switch to another app with the
/// pointer still on a button, and that's missed, so the tooltip stays on screen over everything. Turned on for the
/// whole app by <see cref="Install"/> (App).
/// </summary>
public static class ToolTipCloser
{
    private static readonly HashSet<Control> Open = [];
    private static readonly ConditionalWeakTable<Window, object> Watched = [];   // Loaded again when a hidden window is shown

    public static void Install()
    {
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                Open.Add(control);
            else
                Open.Remove(control);
        });
        Control.LoadedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (!Watched.TryAdd(window, window))
                return;
            window.Deactivated += (_, _) => CloseAll();
            window.PointerExited += (_, _) => CloseAll();
        });
    }

    private static void CloseAll()
    {
        foreach (var control in Open.ToList())
            ToolTip.SetIsOpen(control, false);   // raises the Changed handler above, which takes it out of Open
        Open.Clear();
    }
}
