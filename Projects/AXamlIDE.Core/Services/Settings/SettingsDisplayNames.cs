using System;
using System.Text;

namespace AXamlIDE.Services.Settings;

/// <summary>How Settings names a theme or an editor font: as VS Code does, not as they're stored.</summary>
public static class SettingsDisplayNames
{
    /// <summary>
    /// A built-in theme's name from its TextMate id: words split ("VisualStudioDark" → "Visual Studio Dark"), with
    /// VS Code's names for the default themes ("DarkPlus" → "Dark+") and TextMate's misspelt "Abbys" put right.
    /// </summary>
    public static string Theme(string id) => id switch
    {
        "DarkPlus" => "Dark+",
        "LightPlus" => "Light+",
        "Abbys" => "Abyss",
        _ => SplitWords(id)
    };

    /// <summary>
    /// An editor font setting as a name: its first family, without the "fonts:App#" of a font that ships with the
    /// app ("fonts:App#JetBrains Mono,Cascadia Mono,Consolas" → "JetBrains Mono"). The others are only fallbacks.
    /// </summary>
    public static string Font(string family)
    {
        var first = family.Split(',')[0].Trim();
        var hash = first.IndexOf('#');
        return first.StartsWith("fonts:", StringComparison.OrdinalIgnoreCase) && hash >= 0 ? first[(hash + 1)..] : first;
    }

    /// <summary>"HighContrastDark" → "High Contrast Dark": a space before each capital that starts a new word.</summary>
    private static string SplitWords(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var startsWord = i > 0 && char.IsUpper(name[i])
                             && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]));
            if (startsWord)
                text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }
}
