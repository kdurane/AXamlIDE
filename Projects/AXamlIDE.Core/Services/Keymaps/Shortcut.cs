using System;
using System.Collections.Generic;
using System.Linq;

namespace AXamlIDE.Services.Keymaps;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Meta = 8
}

/// <summary>
/// One key press with its modifiers, e.g. Ctrl+Shift+B. <see cref="Key"/> is the key as written in a keymap file:
/// a letter or digit ("B", "3"), a symbol ("/", ",", "."), or a name ("F5", "Enter", "Delete", "Pause").
/// </summary>
public sealed record KeyStroke(ShortcutModifiers Modifiers, string Key)
{
    /// <summary>"Ctrl+Shift+B": modifiers in Visual Studio's order (Ctrl, Shift, Alt), then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ShortcutModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Meta)) parts.Add("Meta");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}

/// <summary>
/// A keyboard shortcut: one key stroke ("Ctrl+/"), or a chord of two, pressed one after the other
/// ("Ctrl+K, Ctrl+C", Visual Studio's Comment Selection). Written in keymap files the way VS shows them.
/// </summary>
public sealed class Shortcut : IEquatable<Shortcut>
{
    private Shortcut(IReadOnlyList<KeyStroke> strokes) => Strokes = strokes;

    public IReadOnlyList<KeyStroke> Strokes { get; }

    public bool IsChord => Strokes.Count > 1;

    /// <summary>Read "Ctrl+Shift+B" or "Ctrl+K, Ctrl+C". Null when it isn't a shortcut.</summary>
    public static Shortcut? Parse(string text)
    {
        // Strokes are separated by ", ". (A lone "," key is a stroke of its own: "Ctrl+,".)
        var strokes = new List<KeyStroke>();
        foreach (var part in text.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseStroke(part) is not { } stroke)
                return null;
            strokes.Add(stroke);
        }
        return strokes.Count is 1 or 2 ? new Shortcut(strokes) : null;
    }

    private static KeyStroke? ParseStroke(string text)
    {
        var modifiers = ShortcutModifiers.None;
        var rest = text;
        while (true)
        {
            // Take "Ctrl+" (or Shift+, Alt+, Meta+) off the front while there is one. What's left is the key,
            // which may itself be "+" ("Ctrl++").
            var plus = rest.IndexOf('+');
            if (plus <= 0 || plus == rest.Length - 1)
                break;
            var modifier = rest[..plus].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ShortcutModifiers.Ctrl,
                "shift" => ShortcutModifiers.Shift,
                "alt" => ShortcutModifiers.Alt,
                "meta" or "win" or "cmd" => ShortcutModifiers.Meta,
                _ => ShortcutModifiers.None
            };
            if (modifier == ShortcutModifiers.None)
                break;
            modifiers |= modifier;
            rest = rest[(plus + 1)..];
        }

        if (rest.Length == 0 || rest.Contains(' ') || (rest.Length > 1 && rest.Contains('+')))
            return null;   // nothing left, or a modifier we don't know ("Ctrl+", "Super+A")
        var key = rest.Length == 1
            ? rest.ToUpperInvariant()
            : NamedKeys.FirstOrDefault(name => string.Equals(name, rest, StringComparison.OrdinalIgnoreCase));
        if (key is null || (key.Length == 1 && !char.IsLetterOrDigit(key[0]) && !Symbols.Contains(key[0])))
            return null;   // a key we don't know: a typo in a keymap file
        return new KeyStroke(modifiers, key);
    }

    /// <summary>The keys with names, besides letters, digits and <see cref="Symbols"/>. Shell/KeyMap.cs turns them into Avalonia keys.</summary>
    public static IReadOnlyList<string> NamedKeys { get; } =
    [
        "Enter", "Escape", "Space", "Tab", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
        "Up", "Down", "Left", "Right", "Pause",
        .. Enumerable.Range(1, 24).Select(n => $"F{n}")
    ];

    /// <summary>The symbol keys a shortcut can use (the US layout's).</summary>
    public const string Symbols = "/,.`;'[]\\-=+";

    /// <summary>"Ctrl+K, Ctrl+C", the same way Visual Studio writes it.</summary>
    public override string ToString() => string.Join(", ", Strokes);

    public bool Equals(Shortcut? other)
        => other is not null && string.Equals(ToString(), other.ToString(), StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as Shortcut);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(ToString());

    /// <summary>True when <paramref name="other"/> is a chord that starts with this one-stroke shortcut.</summary>
    public bool IsPrefixOf(Shortcut other)
        => !IsChord && other.IsChord && new Shortcut([other.Strokes[0]]).Equals(this);
}
