using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using AXamlIDE.Services.Dialogs;

namespace AXamlIDE.Views;

/// <summary>The main window's clipboard (Avalonia reaches the clipboard through a top-level window).</summary>
public sealed class ClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
