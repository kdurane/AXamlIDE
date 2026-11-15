using System;
using System.Collections.Generic;
using AXamlIDE.Services.Keymaps;
using TextMateSharp.Grammars;

namespace AXamlIDE.Models;

public sealed record EditorSettings
{
    // JetBrains Mono ships with the app (Assets/Fonts, registered in Program.BuildAvaloniaApp); the rest are fallbacks.
    public const string DefaultFontFamily = "fonts:App#JetBrains Mono,Cascadia Mono,Consolas,Menlo,Monospace";

    // The default before JetBrains Mono was bundled. Settings files that still hold it are moved to the new default.
    public const string LegacyDefaultFontFamily = "Cascadia Code,Consolas,Menlo,Monospace";

    // Fonts with a sister family that has no ligatures. AvaloniaEdit doesn't pass font features (-liga/-calt) through
    // to text shaping, so turning ligatures off means swapping to the no-ligature family instead.
    private static readonly Dictionary<string, string> NoLigatureFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fonts:App#JetBrains Mono"] = "fonts:App#JetBrains Mono NL",
        ["JetBrains Mono"] = "JetBrains Mono NL",
        ["Cascadia Code"] = "Cascadia Mono",
    };

    // The font family string to actually render with, given a saved family (a comma-separated fallback list).
    public static string ResolveFontFamily(string fontFamily, bool ligatures)
    {
        if (ligatures)
            return fontFamily;
        var names = fontFamily.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < names.Length; i++)
        {
            if (NoLigatureFamilies.TryGetValue(names[i], out var noLigatures))
                names[i] = noLigatures;
        }
        return string.Join(",", names);
    }

    public string ThemeId { get; init; } = nameof(ThemeName.VisualStudioDark);
    public string FontFamily { get; init; } = DefaultFontFamily;
    public double FontSize { get; init; } = 13;
    public bool ShowLineNumbers { get; init; } = true;
    public bool HighlightCurrentLine { get; init; } = true;
    public bool FontLigatures { get; init; }

    public bool SolutionExplorerOnRight { get; init; }
    public bool FormatOnSave { get; init; }

    /// <summary>The editor helpers that can be switched off (inlay hints, code lens, auto-closing braces...).</summary>
    public EditorFeatureSettings Features { get; init; } = new();

    /// <summary>The bottom panel as it was left: showing or hidden, and the optional tabs that are open.</summary>
    public PanelSettings Panel { get; init; } = new();

    /// <summary>What shows when the IDE starts: the start window (the default, as in Visual Studio), the last workspace, or nothing.</summary>
    public StartupAction Startup { get; init; } = StartupAction.StartWindow;

    /// <summary>The keyboard mapping scheme (Services/Keymaps/&lt;id&gt;.json).</summary>
    public string KeymapId { get; init; } = KeymapLibrary.DefaultId;
}
