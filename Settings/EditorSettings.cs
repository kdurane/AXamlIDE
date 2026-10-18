using TextMateSharp.Grammars;

namespace AXamlIDE.Settings;

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
}
