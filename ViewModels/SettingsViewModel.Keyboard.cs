using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Keymaps;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Settings > Keyboard, like Visual Studio's Tools > Options > Environment > Keyboard: pick a keymap (the
/// mapping scheme), and look up commands and their keys. Choosing a keymap takes effect straight away
/// (Shell/KeyMap.cs listens for <see cref="SelectedKeymap"/>).
/// </summary>
public sealed partial class SettingsViewModel
{
    private KeymapProfile _selectedKeymap;
    private string _shortcutFilter = "";
    private IReadOnlyList<KeyboardShortcutRow> _shortcutRows = [];

    public IReadOnlyList<KeymapProfile> AvailableKeymaps { get; } = KeymapLibrary.BuiltIn;

    public KeymapProfile SelectedKeymap
    {
        get => _selectedKeymap;
        set
        {
            if (value is null || !SetProperty(ref _selectedKeymap, value)) return;
            UpdateEditorSettings(_editorSettings with { KeymapId = value.Id });
            ShortcutRows = BuildShortcutRows();
        }
    }

    /// <summary>"Show commands containing": matches the command, its id, its keys or its description.</summary>
    public string ShortcutFilter
    {
        get => _shortcutFilter;
        set
        {
            if (!SetProperty(ref _shortcutFilter, value ?? "")) return;
            ShortcutRows = BuildShortcutRows();
        }
    }

    public IReadOnlyList<KeyboardShortcutRow> ShortcutRows
    {
        get => _shortcutRows;
        private set => SetProperty(ref _shortcutRows, value);
    }

    private List<KeyboardShortcutRow> BuildShortcutRows()
    {
        var filter = _shortcutFilter.Trim();
        return
        [
            .. CommandCatalog.All
                .Select(command => new KeyboardShortcutRow(
                    command.Category,
                    command.Name,
                    string.Join("  or  ", _selectedKeymap.ShortcutsFor(command.Id)),
                    command.Scope == CommandScope.TextEditor ? "Text Editor" : "Global",
                    command.Description,
                    command.Id))
                .Where(row => filter.Length == 0
                              || row.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)
                              || row.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                              || row.Shortcuts.Contains(filter, StringComparison.OrdinalIgnoreCase)
                              || row.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
        ];
    }
}
