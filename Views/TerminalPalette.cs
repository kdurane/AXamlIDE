using Avalonia.Controls;
using Avalonia.Media;
using Iciclecreek.Terminal;
using XTerm.Options;

namespace AXamlIDE.Views;

/// <summary>
/// Gives the terminal the theme's colours, as VS Code does: the 16 ANSI colours programs print in
/// (<c>terminal.ansiRed</c>...), and the background and foreground (<c>terminal.background</c> /
/// <c>terminal.foreground</c>, else the panel's own). Every theme has the ANSI keys: from its TextMate theme when that
/// sets them, else VS Code's defaults (Styles/ThemeResources.axaml). The terminal takes plain colour values rather
/// than brushes, so this runs again when the theme changes (Views/TerminalView).
/// </summary>
public static class TerminalPalette
{
    private static readonly string[] AnsiNames =
    [
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite"
    ];

    public static void Apply(TerminalControl terminal)
    {
        var background = Find(terminal, "terminal.background") ?? Find(terminal, "sideBar.background") ?? Colors.Black;
        var foreground = Find(terminal, "terminal.foreground") ?? Find(terminal, "editor.foreground") ?? Colors.White;
        var ansi = new string[AnsiNames.Length];
        for (var i = 0; i < AnsiNames.Length; i++)
            ansi[i] = Hex(Find(terminal, "terminal.ansi" + AnsiNames[i]) ?? foreground);

        var theme = new ThemeOptions
        {
            Background = Hex(background),
            Foreground = Hex(foreground),
            Cursor = Hex(foreground),
            Black = ansi[0], Red = ansi[1], Green = ansi[2], Yellow = ansi[3],
            Blue = ansi[4], Magenta = ansi[5], Cyan = ansi[6], White = ansi[7],
            BrightBlack = ansi[8], BrightRed = ansi[9], BrightGreen = ansi[10], BrightYellow = ansi[11],
            BrightBlue = ansi[12], BrightMagenta = ansi[13], BrightCyan = ansi[14], BrightWhite = ansi[15]
        };
        terminal.Background = new SolidColorBrush(background);
        terminal.Foreground = new SolidColorBrush(foreground);
        terminal.CursorColor = foreground;
        terminal.Terminal.Colors.ApplyTheme(theme);
        terminal.Refresh();
    }

    private static Color? Find(Control control, string key)
        => control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is Color color ? color : null;

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
