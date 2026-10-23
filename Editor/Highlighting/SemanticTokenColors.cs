using System.Collections.Generic;
using Avalonia.Media;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// The colour for each kind of semantic token (class, interface, parameter, local...), taken from the editor's TextMate
/// theme. Themes colour TextMate scopes, so a token type is looked up under the scopes VS Code uses for it (its
/// standard fallbacks, plus the C# extension's for Roslyn's own types): a class is coloured like
/// entity.name.type.class, a parameter like variable.parameter. A kind of name the theme has no colour for gets its
/// plain text colour, as in VS Code.
///
/// Only names are coloured. Keywords, strings, comments, numbers and punctuation are left to TextMate, which already
/// gets them right (and the theme's rules for them were written against TextMate's scopes).
/// </summary>
public sealed class SemanticTokenColors
{
    private static readonly Dictionary<string, string[]> Scopes = new()
    {
        ["namespace"] = ["entity.name.namespace"],
        ["type"] = ["entity.name.type", "support.type"],
        ["class"] = ["entity.name.type.class", "support.class"],
        ["recordClass"] = ["entity.name.type.class", "support.class"],
        ["struct"] = ["entity.name.type.struct", "entity.name.type"],
        ["recordStruct"] = ["entity.name.type.struct", "entity.name.type"],
        ["interface"] = ["entity.name.type.interface", "entity.name.type"],
        ["enum"] = ["entity.name.type.enum", "entity.name.type"],
        ["delegate"] = ["entity.name.type.delegate", "entity.name.type"],
        ["typeParameter"] = ["entity.name.type.parameter", "entity.name.type"],
        ["method"] = ["entity.name.function.member", "entity.name.function", "support.function"],
        ["extensionMethod"] = ["entity.name.function.extension", "entity.name.function.member", "entity.name.function"],
        ["function"] = ["entity.name.function", "support.function"],
        ["parameter"] = ["variable.parameter"],
        ["variable"] = ["variable.other.readwrite", "entity.name.variable"],
        ["field"] = ["entity.name.variable.field", "variable.other.field", "variable.other.readwrite"],
        ["property"] = ["variable.other.property", "variable.other.readwrite"],
        ["event"] = ["variable.other.event", "variable.other.property"],
        ["enumMember"] = ["variable.other.enummember", "variable.other.constant"],
        ["constant"] = ["variable.other.constant", "variable.other.readwrite"],
        ["label"] = ["entity.name.label"],
    };

    private readonly Dictionary<string, IBrush> _brushes = [];

    public SemanticTokenColors(IRawTheme rawTheme, IRegistryOptions registry)
    {
        var theme = Theme.CreateFromRawTheme(rawTheme, registry);
        var plainText = theme.GetGuiColorDictionary().TryGetValue("editor.foreground", out var foreground)
                        && Color.TryParse(foreground, out var color)
            ? new SolidColorBrush(color)
            : null;

        foreach (var (type, scopes) in Scopes)
            if (ColorOf(theme, scopes) is { } brush)
                _brushes[type] = brush;
            else if (plainText is not null)
                _brushes[type] = plainText;
    }

    /// <summary>The colour for a token type (the server's name for it), or null to leave it as TextMate coloured it.</summary>
    public IBrush? For(string tokenType) => _brushes.GetValueOrDefault(tokenType);

    /// <summary>The theme's colour for the first of the scopes it has one for, matched the way TextMate colours text.</summary>
    private static IBrush? ColorOf(Theme theme, string[] scopes)
    {
        foreach (var scope in scopes)
            foreach (var rule in theme.Match([scope]))
            {
                // A rule that only applies inside another scope ("meta.class entity.name") can't be checked here.
                if (rule.parentScopes is { Count: > 0 } || rule.foreground <= 0)
                    continue;
                if (Color.TryParse(theme.GetColor(rule.foreground), out var color))
                    return new SolidColorBrush(color);
            }
        return null;
    }
}
