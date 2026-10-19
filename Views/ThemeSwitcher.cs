using System.ComponentModel;
using Avalonia;
using AXamlIDE.Services.Settings;

namespace AXamlIDE.Views;

/// <summary>
/// Colours the whole UI from the theme chosen in Settings, by switching the app's theme variant
/// (<see cref="ThemeVariants"/>). Avalonia then swaps in that theme's colours everywhere they are used as
/// {DynamicResource ...}, floating windows included. The editor's syntax colours come from the same
/// theme through TextMate (Editor/Highlighting/TextMateController).
/// </summary>
public sealed class ThemeSwitcher
{
    private readonly IEditorSettings _settings;

    public ThemeSwitcher(IEditorSettings settings)
    {
        _settings = settings;
        settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSettings.SelectedTheme))
            Apply();
    }

    /// <summary>Switch to the selected theme (call once at startup).</summary>
    public void Apply()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariants.For(_settings.SelectedTheme.Id);
    }
}
