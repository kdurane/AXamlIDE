using Avalonia.Controls;
using Dock.Avalonia.Controls;

namespace AXamlIDE.Views;

/// <summary>
/// The window a tab dragged out of the main window lives in (made by MainWindow.CreateFloatingWindow). Dock's
/// HostWindow, styled as HostWindow (its style key), apart from one thing when the main window closes.
/// </summary>
public class FloatingWindow : HostWindow
{
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Avalonia asks owned windows (Search's) before the main window, and any that cancels stops the main
        // window's close before its own Closing handler runs. Ours always cancels (it moves the tabs back first,
        // see DockLayoutViewModel.OnWindowClosing), so the main window's X would only close this window. Leave it
        // to the main window: it asks its questions and then closes the floating windows itself.
        if (e.CloseReason == WindowCloseReason.OwnerWindowClosing)
            return;
        base.OnClosing(e);
    }
}
