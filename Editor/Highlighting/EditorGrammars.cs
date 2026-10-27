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
    private const string CSharpColorsUri = "avares://AXamlIDE/Assets/Grammars/csharp-colors.json";
    private static readonly HashSet<string> AxamlScopedExtensions =
    new([".axaml", ".xml", ".manifest", ".csproj", ".props", ".targets"], StringComparer.OrdinalIgnoreCase);

    private IRawGrammar? _axamlGrammar;
    private readonly Dictionary<string, IRawTheme> _colorLayers = [];

    // Every editor pane (one per open tab) asks for the theme when it opens and again when the theme changes, and
    // builds its semantic colours from it. The themes are fixed assets, so one copy is loaded and shared rather than
    // each pane parsing the same files again. Only the current one is kept: there's one theme at a time.
    private (string Id, IRawTheme Theme)? _loadedTheme;
    private (IRawTheme Theme, SemanticTokenColors Colors)? _semanticColors;

    /// <summary>The grammar scope for a file, or null when there's no grammar for it.</summary>
    public string? ScopeForFile(string? path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return null;
        if (AxamlScopedExtensions.Contains(extension))
            return AxamlScope;

        return builtIn.GetLanguageByExtension(extension) is { } language
            ? builtIn.GetScopeByLanguageId(language.Id)
            : null;
    }

    /// <summary>The theme for the editors: the chosen theme with the AXAML colours for its Dark/Light base on top.</summary>
    public IRawTheme LoadTheme(EditorTheme theme)
    {
        if (_loadedTheme is { } loaded && loaded.Id == theme.Id)
            return loaded.Theme;

        var baseTheme = theme.Load(builtIn);
        var variant = ThemeVariants.For(theme.Id).InheritVariant == ThemeVariant.Dark ? "dark" : "light";
        var layered = new LayeredTheme(baseTheme, under: ReadColorLayer(CSharpColorsUri, variant), over: ReadColorLayer(AxamlColorsUri, variant));
        _loadedTheme = (theme.Id, layered);
        return layered;
    }

    /// <summary>The semantic-token brushes for a theme from <see cref="LoadTheme"/>, built once and shared by the panes.</summary>
    public SemanticTokenColors SemanticColorsFor(IRawTheme theme)
    {
        if (_semanticColors is { } built && ReferenceEquals(built.Theme, theme))
            return built.Colors;
        var colors = new SemanticTokenColors(theme, this);
        _semanticColors = (theme, colors);
        return colors;
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

    /// <summary>The "dark" or "light" rules of one of our colour files (axaml-colors.json, csharp-colors.json), read as a theme of tokenColors.</summary>
    private IRawTheme ReadColorLayer(string uri, string variant)
    {
        var cacheKey = uri + "|" + variant;   // both files have a "dark" and a "light"
        if (_colorLayers.TryGetValue(cacheKey, out var cached))
            return cached;

        using var stream = AssetLoader.Open(new Uri(uri));
        var rules = JsonNode.Parse(stream)?[variant]?.ToJsonString() ?? "[]";   // in the file it's just "dark" / "light"
        var json = $$"""{ "name": "{{cacheKey}}", "tokenColors": {{rules}} }""";
        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        return _colorLayers[cacheKey] = ThemeReader.ReadThemeSync(reader);
    }

    /// <summary>
    /// A theme plus extra token rules. TextMateSharp reads a theme's "settings" then its "tokenColors", and a later
    /// rule wins over an earlier one for the same scope, so the extra rules go last.
    /// </summary>
    private sealed class LayeredTheme(IRawTheme theme, IRawTheme under, IRawTheme over) : IRawTheme
    {
        public string GetName() => theme.GetName();
        public string GetInclude() => theme.GetInclude();
        // First, so any rule of the theme's for the same scope comes later and wins.
        public ICollection<IRawThemeSetting> GetSettings() => [.. under.GetTokenColors() ?? [], .. theme.GetSettings() ?? []];
        public ICollection<IRawThemeSetting> GetTokenColors() => [.. theme.GetTokenColors() ?? [], .. over.GetTokenColors() ?? []];
        public ICollection<KeyValuePair<string, object>> GetGuiColors() => theme.GetGuiColors();
    }
}
