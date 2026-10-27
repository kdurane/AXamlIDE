using AXamlIDE.Services.Keymaps;
using TextMateSharp.Grammars;

namespace AXamlIDE.Models;

public sealed record EditorSettings
{
    public const string DefaultFontFamily = "Cascadia Code,Consolas,Menlo,Monospace";

    public string ThemeId { get; init; } = nameof(ThemeName.VisualStudioDark);
    public string FontFamily { get; init; } = DefaultFontFamily;
    public double FontSize { get; init; } = 13;
    public bool ShowLineNumbers { get; init; } = true;
    public bool HighlightCurrentLine { get; init; } = true;
    public bool SolutionExplorerOnRight { get; init; }
    public bool FormatOnSave { get; init; }

    /// <summary>The editor helpers that can be switched off (inlay hints, code lens, auto-closing braces...).</summary>
    public EditorFeatureSettings Features { get; init; } = new();

    /// <summary>The bottom panel as it was left: showing or hidden, and the optional tabs that are open.</summary>
    public PanelSettings Panel { get; init; } = new();

    /// <summary>The keyboard mapping scheme (Services/Keymaps/&lt;id&gt;.json).</summary>
    public string KeymapId { get; init; } = KeymapLibrary.DefaultId;
}
