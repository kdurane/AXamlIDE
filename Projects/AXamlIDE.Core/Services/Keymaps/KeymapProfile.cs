using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace AXamlIDE.Services.Keymaps;

/// <summary>
/// A keymap: which shortcuts each command gets, like Visual Studio's keyboard mapping schemes. Read from a
/// JSON file shaped like this (a command takes one shortcut or a list of them):
/// <code>
/// {
///   "name": "Visual Studio",
///   "bindings": {
///     "Edit.ToggleComment": ["Ctrl+/", "Ctrl+K, Ctrl+/"],
///     "Build.Build": "Ctrl+Shift+B"
///   }
/// }
/// </code>
/// </summary>
public sealed record KeymapProfile(string Id, string Name, IReadOnlyDictionary<string, IReadOnlyList<Shortcut>> Bindings)
{
    /// <summary>The shortcuts for a command; empty when it has none in this keymap.</summary>
    public IReadOnlyList<Shortcut> ShortcutsFor(string commandId)
        => Bindings.GetValueOrDefault(commandId) ?? [];

    /// <summary>
    /// Read a keymap file. Problems (an unknown command id, a shortcut that doesn't parse) are skipped and
    /// listed in <paramref name="warnings"/>, so one typo doesn't lose the whole keymap.
    /// </summary>
    public static KeymapProfile Parse(string id, string json, List<string> warnings)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var root = document.RootElement;
        var name = root.TryGetProperty("name", out var nameElement) && nameElement.GetString() is { } text ? text : id;

        var bindings = new Dictionary<string, IReadOnlyList<Shortcut>>();
        if (root.TryGetProperty("bindings", out var bindingsElement) && bindingsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var binding in bindingsElement.EnumerateObject())
            {
                if (CommandCatalog.Find(binding.Name) is null)
                {
                    warnings.Add($"Keymap '{name}': unknown command '{binding.Name}'.");
                    continue;
                }

                var texts = binding.Value.ValueKind == JsonValueKind.Array
                    ? binding.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : [binding.Value.GetString() ?? ""];
                var shortcuts = new List<Shortcut>();
                foreach (var shortcutText in texts)
                {
                    if (Shortcut.Parse(shortcutText) is { } shortcut)
                        shortcuts.Add(shortcut);
                    else
                        warnings.Add($"Keymap '{name}': '{shortcutText}' for {binding.Name} isn't a shortcut.");
                }
                bindings[binding.Name] = shortcuts;
            }
        }
        return new KeymapProfile(id, name, bindings);
    }
}

/// <summary>
/// The keymaps the IDE ships with (Services/Keymaps/*.json, embedded in the assembly). The file name is the id
/// that settings.json stores. A folder of the user's own keymaps can be added here later, the way SnippetLibrary
/// adds the user's snippets.
/// </summary>
public static class KeymapLibrary
{
    public const string DefaultId = "visual-studio";

    private const string ResourcePrefix = "AXamlIDE.Keymaps.";
    private static readonly Lazy<(IReadOnlyList<KeymapProfile> Profiles, IReadOnlyList<string> Warnings)> Loaded = new(Load);

    /// <summary>The built-in keymaps, Visual Studio first.</summary>
    public static IReadOnlyList<KeymapProfile> BuiltIn => Loaded.Value.Profiles;

    /// <summary>Problems found reading the built-in keymaps (a test keeps this empty).</summary>
    public static IReadOnlyList<string> Warnings => Loaded.Value.Warnings;

    /// <summary>The keymap with this id, or the default one.</summary>
    public static KeymapProfile Get(string? id)
        => BuiltIn.FirstOrDefault(p => p.Id == id) ?? BuiltIn.First(p => p.Id == DefaultId);

    public static bool IsKnown(string? id) => BuiltIn.Any(p => p.Id == id);

    private static (IReadOnlyList<KeymapProfile>, IReadOnlyList<string>) Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var warnings = new List<string>();
        var profiles = new List<KeymapProfile>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var id = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);
            profiles.Add(KeymapProfile.Parse(id, new StreamReader(stream).ReadToEnd(), warnings));
        }
        return ([.. profiles.OrderBy(p => p.Id != DefaultId).ThenBy(p => p.Name)], warnings);
    }
}
