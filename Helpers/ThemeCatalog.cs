using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace AXaxmlIDE.Helpers;

public sealed record ThemeChoice(string Id, string Display, Func<RegistryOptions, IRawTheme> Load);

public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeChoice> All { get; } = Build();

    static List<ThemeChoice> Build()
    {
        var list = new List<ThemeChoice>();

        // every built-in theme, Id = enum name
        foreach (var name in Enum.GetValues<ThemeName>())
        {
            var n = name; // capture
            list.Add(new ThemeChoice(n.ToString(), n.ToString(), r => r.LoadTheme(n)));
        }

        list.Add(Custom("AxamlDark", "AXAML Dark",
            "avares://AXaxmlIDE/Assets/Themes/axaml-dark.json"));

        return list;
    }

    static ThemeChoice Custom(string id, string display, string avaresUri) =>
        new(id, display, _ =>
        {
            using var stream = AssetLoader.Open(new Uri(avaresUri));
            using var reader = new StreamReader(stream);
            return ThemeReader.ReadThemeSync(reader);
        });

    public static ThemeChoice Find(string? id) =>
        All.FirstOrDefault(t => t.Id == id)
        ?? All.First(t => t.Id == nameof(ThemeName.DarkPlus));
}