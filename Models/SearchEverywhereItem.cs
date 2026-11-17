using System;
using System.Windows.Input;
using AXamlIDE.Services.Keymaps;

namespace AXamlIDE.Models;

/// <summary>Search Everywhere's tabs, as Rider's: All, Types (Rider: Classes), Files, Symbols, Actions, Text.</summary>
public enum SearchEverywhereTab
{
    All,
    Types,
    Files,
    Symbols,
    Actions,
    Text
}

/// <summary>
/// An IDE command Search Everywhere can run (Rider's "Actions" tab, VS's feature search): its catalog entry, the
/// command behind it, and its key in the active keymap (null when it has none). Made by <c>Shell.KeyMap.Actions</c>.
/// </summary>
public sealed record IdeAction(IdeCommandInfo Info, ICommand Command, object? Parameter, string? Shortcut);

/// <summary>
/// One row of Search Everywhere's results: a type, file, symbol, action, option or the "find in files" row.
/// <see cref="Section"/> is shown above the first row of each section on the All tab.
/// </summary>
public sealed class SearchEverywhereItem
{
    public required string Title { get; init; }

    /// <summary>Greyed after the title: a folder, the containing type, an action's category.</summary>
    public string Detail { get; init; } = "";

    /// <summary>On the right: an action's key, an option's On/Off.</summary>
    public string? Shortcut { get; init; }

    /// <summary>An icon resource key (Icon.File...).</summary>
    public string Icon { get; init; } = "Icon.File";

    public required string Section { get; init; }

    /// <summary>The first row of its section on the All tab: the section's name goes above it.</summary>
    public bool ShowsSection { get; set; }

    /// <summary>
    /// What choosing it does. The popup closes first and the focus goes back where it was (the editor), so an
    /// editor action works on the editor. Options don't close the popup (<see cref="KeepsOpen"/>).
    /// </summary>
    public required Action Run { get; init; }

    /// <summary>An option row: choosing it switches the option and the list stays, as in Rider.</summary>
    public bool KeepsOpen { get; init; }

    /// <summary>How well it matched (FuzzyMatch); the best come first within a section.</summary>
    public int Score { get; init; }
}
