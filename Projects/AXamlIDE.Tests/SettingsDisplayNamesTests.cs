using AXamlIDE.Services.Settings;

namespace AXamlIDE.Tests;

public sealed class SettingsDisplayNamesTests
{
    [Theory]
    [InlineData("VisualStudioDark", "Visual Studio Dark")]
    [InlineData("HighContrastLight", "High Contrast Light")]
    [InlineData("Monokai", "Monokai")]
    [InlineData("DarkPlus", "Dark+")]
    [InlineData("LightPlus", "Light+")]
    [InlineData("Abbys", "Abyss")]
    [InlineData("AtomOneDark", "Atom One Dark")]
    public void Themes_are_named_as_VS_Code_names_them(string id, string expected)
        => Assert.Equal(expected, SettingsDisplayNames.Theme(id));

    [Theory]
    [InlineData("fonts:App#JetBrains Mono,Cascadia Mono,Consolas,Menlo,Monospace", "JetBrains Mono")]
    [InlineData("Consolas,Menlo,Monospace", "Consolas")]
    [InlineData("Monospace", "Monospace")]
    [InlineData(" Fira Code , Monospace", "Fira Code")]
    public void A_font_setting_is_named_by_its_first_family(string family, string expected)
        => Assert.Equal(expected, SettingsDisplayNames.Font(family));
}
