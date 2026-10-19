using Avalonia;
using Avalonia.Media;

namespace AXamlIDE.Views;

/// <summary>
/// The current theme's brushes (Styles/ThemeResources.axaml), for code that draws or builds its own visuals
/// (editor margins, renderers, the hover card). Look them up when drawing, not once, so a theme switch shows.
/// </summary>
public static class ThemeBrushes
{
    public static IBrush? Get(string key)
        => Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true
            ? brush as IBrush
            : null;
}
