using System.Linq;
using Avalonia.Styling;
using AXamlIDE.Services.Settings;
using TextMateSharp.Grammars;

namespace AXamlIDE.Views;

/// <summary>
/// One Avalonia <see cref="ThemeVariant"/> per theme in Settings, each built on Dark or Light. Selecting a theme
/// sets <c>Application.RequestedThemeVariant</c> to its variant, and Avalonia swaps in that theme's colours
/// (Styles/Themes/&lt;Id&gt;.axaml, listed in Styles/ThemeResources.axaml). Fluent and Dock fall back to their
/// Dark or Light look for anything the theme doesn't set.
///
/// To add a theme: add a field here (and to <see cref="All"/>), a Styles/Themes/&lt;Id&gt;.axaml with the same
/// keys as the others, and a line in Styles/ThemeResources.axaml.
/// </summary>
public static class ThemeVariants
{
    // Variant keys are prefixed because TextMate's "Dark" and "Light" themes would otherwise be Avalonia's own variants.
    public static readonly ThemeVariant Abbys = OnDark(nameof(ThemeName.Abbys));
    public static readonly ThemeVariant Dark = OnDark(nameof(ThemeName.Dark));
    public static readonly ThemeVariant DarkPlus = OnDark(nameof(ThemeName.DarkPlus));
    public static readonly ThemeVariant DimmedMonokai = OnDark(nameof(ThemeName.DimmedMonokai));
    public static readonly ThemeVariant KimbieDark = OnDark(nameof(ThemeName.KimbieDark));
    public static readonly ThemeVariant Light = OnLight(nameof(ThemeName.Light));
    public static readonly ThemeVariant LightPlus = OnLight(nameof(ThemeName.LightPlus));
    public static readonly ThemeVariant OneDark = OnDark(nameof(ThemeName.OneDark));
    public static readonly ThemeVariant Monokai = OnDark(nameof(ThemeName.Monokai));
    public static readonly ThemeVariant QuietLight = OnLight(nameof(ThemeName.QuietLight));
    public static readonly ThemeVariant Red = OnDark(nameof(ThemeName.Red));
    public static readonly ThemeVariant SolarizedDark = OnDark(nameof(ThemeName.SolarizedDark));
    public static readonly ThemeVariant SolarizedLight = OnLight(nameof(ThemeName.SolarizedLight));
    public static readonly ThemeVariant TomorrowNightBlue = OnDark(nameof(ThemeName.TomorrowNightBlue));
    public static readonly ThemeVariant HighContrastLight = OnLight(nameof(ThemeName.HighContrastLight));
    public static readonly ThemeVariant HighContrastDark = OnDark(nameof(ThemeName.HighContrastDark));
    public static readonly ThemeVariant Dracula = OnDark(nameof(ThemeName.Dracula));
    public static readonly ThemeVariant AtomOneLight = OnLight(nameof(ThemeName.AtomOneLight));
    public static readonly ThemeVariant AtomOneDark = OnDark(nameof(ThemeName.AtomOneDark));
    public static readonly ThemeVariant VisualStudioLight = OnLight(nameof(ThemeName.VisualStudioLight));
    public static readonly ThemeVariant VisualStudioDark = OnDark(nameof(ThemeName.VisualStudioDark));
    public static readonly ThemeVariant AxamlDark = OnDark(ThemeIds.AxamlDark);

    private static readonly ThemeVariant[] All =
    [
        Abbys, Dark, DarkPlus, DimmedMonokai, KimbieDark, Light, LightPlus, OneDark, Monokai, QuietLight, Red,
        SolarizedDark, SolarizedLight, TomorrowNightBlue, HighContrastLight, HighContrastDark, Dracula,
        AtomOneLight, AtomOneDark, VisualStudioLight, VisualStudioDark, AxamlDark
    ];

    /// <summary>The variant for a Settings theme id (<see cref="Models.EditorTheme.Id"/>); Dark+ if unknown.</summary>
    public static ThemeVariant For(string themeId)
        => All.FirstOrDefault(variant => Equals(variant.Key, KeyFor(themeId))) ?? DarkPlus;

    private static string KeyFor(string themeId) => "Theme." + themeId;

    private static ThemeVariant OnDark(string themeId) => new(KeyFor(themeId), ThemeVariant.Dark);

    private static ThemeVariant OnLight(string themeId) => new(KeyFor(themeId), ThemeVariant.Light);
}
