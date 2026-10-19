using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia.Platform;
using Avalonia.Styling;
using AXamlIDE.Models;
using AXamlIDE.Views;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// The editor's TextMate grammars and themes: TextMateSharp's built-in ones, plus our own AXAML grammar
/// (Assets/Grammars/axaml.tmLanguage.json, used for .axaml instead of the generic XML one) and the AXAML
/// colours laid over every theme (Assets/Grammars/axaml-colors.json, a "dark" and a "light" set).
/// Editors install TextMate with this as the registry, so the AXAML grammar can be found by its scope.
/// </summary>
public sealed class EditorGrammars(RegistryOptions builtIn) : IRegistryOptions
{
    public const string AxamlScope = "text.xml.axaml";
    private const string AxamlGrammarUri = "avares://AXamlIDE/Assets/Grammars/axaml.tmLanguage.json";
    private const string AxamlColorsUri = "avares://AXamlIDE/Assets/Grammars/axaml-colors.json";

    private IRawGrammar? _axamlGrammar;
    private readonly Dictionary<string, IRawTheme> _axamlColors = [];

    /// <summary>The grammar scope for a file, or null when there's no grammar for it.</summary>
    public string? ScopeForFile(string? path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return null;
        if (extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase))
            return AxamlScope;

        return builtIn.GetLanguageByExtension(extension) is { } language
            ? builtIn.GetScopeByLanguageId(language.Id)
            : null;
    }

    /// <summary>The theme for the editors: the chosen theme with the AXAML colours for its Dark/Light base on top.</summary>
    public IRawTheme LoadTheme(EditorTheme theme)
    {
        var baseTheme = theme.Load(builtIn);
        var isDark = ThemeVariants.For(theme.Id).InheritVariant == ThemeVariant.Dark;
        return new LayeredTheme(baseTheme, AxamlColors(isDark ? "dark" : "light"));
    }

    // ---------------------------------------------------------------- IRegistryOptions

    public IRawGrammar GetGrammar(string scopeName)
        => scopeName == AxamlScope ? _axamlGrammar ??= ReadAxamlGrammar() : builtIn.GetGrammar(scopeName);

    public IRawTheme GetTheme(string scopeName) => builtIn.GetTheme(scopeName);
    public ICollection<string> GetInjections(string scopeName) => builtIn.GetInjections(scopeName);
    public IRawTheme GetDefaultTheme() => builtIn.GetDefaultTheme();

    // ---------------------------------------------------------------- assets

    private static IRawGrammar ReadAxamlGrammar()
    {
        using var reader = new StreamReader(AssetLoader.Open(new Uri(AxamlGrammarUri)));
        return GrammarReader.ReadGrammarSync(reader);
    }

    /// <summary>The "dark" or "light" rules of axaml-colors.json, read as a theme of tokenColors.</summary>
    private IRawTheme AxamlColors(string variant)
    {
        if (_axamlColors.TryGetValue(variant, out var cached))
            return cached;

        using var stream = AssetLoader.Open(new Uri(AxamlColorsUri));
        var rules = JsonNode.Parse(stream)?[variant]?.ToJsonString() ?? "[]";
        var json = $$"""{ "name": "axaml-colors-{{variant}}", "tokenColors": {{rules}} }""";
        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        return _axamlColors[variant] = ThemeReader.ReadThemeSync(reader);
    }

    /// <summary>
    /// A theme plus extra token rules. TextMateSharp reads a theme's "settings" then its "tokenColors", and a later
    /// rule wins over an earlier one for the same scope, so the extra rules go last.
    /// </summary>
    private sealed class LayeredTheme(IRawTheme theme, IRawTheme extra) : IRawTheme
    {
        public string GetName() => theme.GetName();
        public string GetInclude() => theme.GetInclude();
        public ICollection<IRawThemeSetting> GetSettings() => theme.GetSettings();
        public ICollection<IRawThemeSetting> GetTokenColors() => [.. theme.GetTokenColors() ?? [], .. extra.GetTokenColors() ?? []];
        public ICollection<KeyValuePair<string, object>> GetGuiColors() => theme.GetGuiColors();
    }
}
