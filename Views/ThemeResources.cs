using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using AXamlIDE.Services.Settings;
using TextMateSharp.Grammars;
using TextMateSharp.Themes;

namespace AXamlIDE.Views;

/// <summary>
/// Colours the whole UI from the theme chosen in Settings (the "colors" section of a VS Code theme, e.g.
/// Assets/axaml-dark.json), not just the editor. Each theme colour is copied into app-wide resources:
/// Dock's (tabs, panels, headers), Fluent's (lists, drop-downs, menus) and our own App* brushes used by
/// our views (MainWindow's title bar, activity bar and status bar, the document bar...). Every view uses
/// them as {DynamicResource ...}, so a theme change applies everywhere at once, floating windows included.
///
/// To theme another part: find its theme key (VS Code's theme colour reference) and add a line to Map.
/// A key the theme doesn't define leaves the resource as it was (Dock's preset, Fluent's default).
/// </summary>
public sealed class ThemeResources
{
    /// <summary>Theme colour key -> the resources it sets.</summary>
    private static readonly (string ThemeKey, string[] Resources)[] Map =
    [
        ("editor.background", ["AppWindowBackgroundBrush", "DockSurfaceEditorBrush", "DockSurfaceWorkbenchBrush"]),
        ("editor.foreground", ["AppForegroundBrush", "ThemeForegroundBrush"]),

        ("sideBar.background", ["ThemeBackgroundBrush", "DockSurfaceSidebarBrush", "DockSurfacePanelBrush",
            "AppDocumentBarBackgroundBrush", "TreeViewItemBackground", "ListBoxItemBackground"]),
        ("sideBar.foreground", ["TreeViewItemForeground", "ListBoxItemForeground"]),
        ("sideBarSectionHeader.background", ["DockSurfaceHeaderBrush"]),
        ("sideBarSectionHeader.foreground", ["DockWindowChromeForegroundBrush", "DockChromeButtonForegroundBrush"]),

        ("tab.inactiveBackground", ["DockTabBackgroundBrush"]),
        ("tab.inactiveForeground", ["DockTabForegroundBrush"]),
        ("tab.activeForeground", ["DockTabActiveForegroundBrush", "DockTabSelectedForegroundBrush",
            "DockDocumentTabSelectedForegroundBrush", "DockDocumentTabPointerOverForegroundBrush"]),
        ("tab.border", ["DockBorderSubtleBrush", "DockSeparatorBrush", "DockTabActiveIndicatorBrush"]),
        ("list.hoverBackground", ["DockTabHoverBackgroundBrush", "TreeViewItemBackgroundPointerOver",
            "ListBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver"]),
        ("list.inactiveSelectionBackground", ["TreeViewItemBackgroundSelected", "ListBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundSelected"]),
        ("list.activeSelectionBackground", ["TreeViewItemBackgroundSelectedPointerOver",
            "ListBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPointerOver"]),
        ("list.activeSelectionForeground", ["TreeViewItemForegroundSelected", "ListBoxItemForegroundSelected",
            "ComboBoxItemForegroundSelected"]),

        ("titleBar.activeBackground", ["AppTitleBarBackgroundBrush", "DockWindowChromeTitleBarBackgroundBrush"]),
        ("titleBar.activeForeground", ["AppTitleBarForegroundBrush"]),
        ("activityBar.background", ["AppActivityBarBackgroundBrush"]),
        ("activityBar.foreground", ["AppActivityBarForegroundBrush"]),
        ("statusBar.background", ["AppStatusBarBackgroundBrush"]),
        ("statusBar.foreground", ["AppStatusBarForegroundBrush"]),

        ("dropdown.background", ["ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxDropDownBackground",
            "MenuFlyoutPresenterBackground", "ComboBoxItemBackground"]),
        ("dropdown.foreground", ["ComboBoxForeground", "ComboBoxItemForeground", "MenuFlyoutItemForeground"]),
        ("dropdown.border", ["ComboBoxBorderBrush", "ComboBoxDropDownBorderBrush"]),
    ];

    private readonly IEditorSettings _settings;
    private readonly RegistryOptions _registryOptions;

    public ThemeResources(IEditorSettings settings, RegistryOptions registryOptions)
    {
        _settings = settings;
        _registryOptions = registryOptions;
        settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSettings.SelectedTheme))
            Apply();
    }

    /// <summary>Copy the selected theme's colours into the app's resources (call once at startup).</summary>
    public void Apply()
    {
        if (Application.Current is not { } app)
            return;

        var colours = ReadColours();
        foreach (var (themeKey, resources) in Map)
        {
            if (!colours.TryGetValue(themeKey, out var colour))
                continue;
            foreach (var resource in resources)
                app.Resources[resource] = new SolidColorBrush(colour);
        }

        // The selected tab, a touch lighter than the editor it belongs to, so it's easy to spot.
        if (colours.TryGetValue("tab.activeBackground", out var active) && colours.TryGetValue("tab.activeForeground", out var text))
        {
            app.Resources["DockTabActiveBackgroundBrush"] = new SolidColorBrush(Mix(active, text, 0.12));
            app.Resources["DockSurfaceHeaderActiveBrush"] = new SolidColorBrush(Mix(active, text, 0.08));
        }
    }

    /// <summary>
    /// The theme's UI colours, including those it inherits from an included theme (built-in themes
    /// like Dark+ keep most of theirs in a base theme). Resolved the same way the editor's TextMate does.
    /// </summary>
    private Dictionary<string, Color> ReadColours()
    {
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        var theme = Theme.CreateFromRawTheme(_settings.SelectedTheme.Load(_registryOptions), _registryOptions);
        foreach (var (key, value) in theme.GetGuiColorDictionary())
            if (value is { } text && Color.TryParse(text, out var colour))
                colours[key] = colour;
        return colours;
    }

    /// <summary><paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.A,
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
