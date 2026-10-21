using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Services.Keymaps;

namespace AXamlIDE.Tests;

public sealed class KeymapTests
{
    [Theory]
    [InlineData("Ctrl+Shift+B", "Ctrl+Shift+B")]
    [InlineData("shift+ctrl+b", "Ctrl+Shift+B")]
    [InlineData("Ctrl+K, Ctrl+C", "Ctrl+K, Ctrl+C")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Ctrl+/", "Ctrl+/")]
    [InlineData("f5", "F5")]
    [InlineData("alt+pagedown", "Alt+PageDown")]
    public void Shortcuts_parse_and_print_the_Visual_Studio_way(string text, string expected)
        => Assert.Equal(expected, Shortcut.Parse(text)?.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+K, Ctrl+C, Ctrl+X")]
    [InlineData("Ctrl+Page Down")]
    [InlineData("Ctrl+Fiv")]
    [InlineData("Ctrl+£")]
    public void Not_shortcuts(string text) => Assert.Null(Shortcut.Parse(text));

    [Fact]
    public void A_single_key_is_the_prefix_of_a_chord_starting_with_it()
    {
        Assert.True(Shortcut.Parse("Ctrl+K")!.IsPrefixOf(Shortcut.Parse("Ctrl+K, Ctrl+C")!));
        Assert.False(Shortcut.Parse("Ctrl+J")!.IsPrefixOf(Shortcut.Parse("Ctrl+K, Ctrl+C")!));
    }

    [Fact]
    public void Built_in_keymaps_load_without_warnings()
    {
        Assert.Empty(KeymapLibrary.Warnings);
        Assert.Equal(KeymapLibrary.DefaultId, KeymapLibrary.BuiltIn[0].Id);
        Assert.Contains(KeymapLibrary.BuiltIn, p => p.Id == "vs-code");
    }

    [Fact]
    public void Toggle_comment_is_ctrl_slash_in_every_built_in_keymap()
    {
        foreach (var profile in KeymapLibrary.BuiltIn)
            Assert.Contains(Shortcut.Parse("Ctrl+/"), profile.ShortcutsFor("Edit.ToggleComment"));
    }

    /// <summary>
    /// No shortcut does two things, and no single key is also the start of a chord (Ctrl+K would run before
    /// Ctrl+K, Ctrl+C could). Commands with different scopes may share a key only if one is editor-only.
    /// </summary>
    [Fact]
    public void Built_in_keymaps_have_no_clashes()
    {
        foreach (var profile in KeymapLibrary.BuiltIn)
        {
            var all = profile.Bindings
                .SelectMany(b => b.Value.Select(s => (Command: b.Key, Shortcut: s)))
                .ToList();
            var problems = new List<string>();
            foreach (var a in all)
            foreach (var b in all)
            {
                if (a.Command == b.Command)
                    continue;
                if (a.Shortcut.Equals(b.Shortcut) && string.CompareOrdinal(a.Command, b.Command) < 0)
                    problems.Add($"{profile.Name}: {a.Shortcut} is both {a.Command} and {b.Command}");
                if (a.Shortcut.IsPrefixOf(b.Shortcut))
                    problems.Add($"{profile.Name}: {a.Shortcut} ({a.Command}) starts the chord {b.Shortcut} ({b.Command})");
            }
            Assert.Empty(problems);
        }
    }

    [Fact]
    public void Every_command_has_a_category_and_a_unique_id()
    {
        Assert.Equal(CommandCatalog.All.Count, CommandCatalog.All.Select(c => c.Id).Distinct().Count());
        Assert.All(CommandCatalog.All, c => Assert.False(string.IsNullOrEmpty(c.Category)));
    }
}
