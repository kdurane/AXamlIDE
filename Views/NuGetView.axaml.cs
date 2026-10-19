using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AXamlIDE.Views;

/// <summary>The NuGet manager tab (DataContext: ViewModels/NuGetDockable). Code-behind only opens links.</summary>
public partial class NuGetView : UserControl
{
    public NuGetView() => InitializeComponent();

    /// <summary>Licence and project links open in the browser.</summary>
    private async void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && TopLevel.GetTopLevel(this) is { } topLevel)
            await topLevel.Launcher.LaunchUriAsync(uri);
    }
}
