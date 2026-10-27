using Avalonia;
using Avalonia.Data.Converters;

namespace AXamlIDE.Views;

/// <summary>How a recent entry looks in the start window: a solution or folder icon, the pin's fill, and a missing entry faded.</summary>
public static class StartConverters
{
    public static readonly IValueConverter Icon =
        new FuncValueConverter<bool, object?>(isSolution => Resource(isSolution ? "Icon.Solution" : "Icon.FolderOutline"));

    public static readonly IValueConverter PinIcon =
        new FuncValueConverter<bool, object?>(pinned => Resource(pinned ? "Icon.Pin" : "Icon.PinOutline"));

    public static readonly IValueConverter MissingOpacity =
        new FuncValueConverter<bool, double>(missing => missing ? 0.5 : 1.0);

    private static object? Resource(string key)
        => Application.Current?.TryGetResource(key, null, out var value) == true ? value : AvaloniaProperty.UnsetValue;
}
