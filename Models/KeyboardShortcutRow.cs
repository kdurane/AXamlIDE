namespace AXamlIDE.Models;

/// <summary>One line of Settings > Keyboard: a command and its keys in the selected keymap.</summary>
public sealed record KeyboardShortcutRow(string Category, string Command, string Shortcuts, string AppliesIn, string Description, string Id);
