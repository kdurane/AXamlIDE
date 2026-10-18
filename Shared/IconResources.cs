using Avalonia.Controls;
using Avalonia.Media;

namespace AXamlIDE.Shared;

internal static class IconResources
{
    public static Geometry? Get(Control host, string key) =>
        host.TryFindResource(key, host.ActualThemeVariant, out var resource)
            ? resource as Geometry
            : null;
}
