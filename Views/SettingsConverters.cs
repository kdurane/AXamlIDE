using Avalonia.Data.Converters;
using AXamlIDE.Services.Settings;

namespace AXamlIDE.Views;

/// <summary>How Settings shows what it stores.</summary>
public static class SettingsConverters
{
    /// <summary>An editor font setting by its first family's name ("JetBrains Mono"), not the stored fallback list.</summary>
    public static readonly IValueConverter FontName = new FuncValueConverter<string, string>(family => SettingsDisplayNames.Font(family ?? ""));
}
