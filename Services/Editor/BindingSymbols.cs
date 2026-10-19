using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// The C# behind an AXAML binding: where a bound view-model property (XamlCompletionService.BindingTarget) is
/// declared, and its hover, asked of the C# language server. Used by AXAML hover and F12.
/// </summary>
public static partial class BindingSymbols
{
    /// <summary>
    /// Where the property is written in the workspace: its declaration; for an [ObservableProperty] field (the
    /// property only exists in generated code) the field, as Rider does; else the generated declaration. Null when
    /// there's no C# server or no match. Comes with the client that found them.
    /// </summary>
    public static async Task<(LspClient Client, IReadOnlyList<LspLocation> Declarations)?> FindAsync(
        LanguageSessionService languages, XamlCompletionService.BindingTarget target)
    {
        if (await SymbolsAsync(languages, target) is not var (client, symbols))
            return null;
        var properties = symbols.Where(symbol => symbol.Name == target.Property).ToList();
        if (properties.Count == 0)
            return null;

        var written = properties.Where(symbol => !IsGenerated(symbol.Uri)).ToList();
        if (written.Count == 0)
            written = [.. symbols.Where(symbol => IsBackingField(symbol.Name, target.Property) && !IsGenerated(symbol.Uri))];
        return (client, written.Count > 0 ? written : properties);
    }

    /// <summary>The C# hover of the bound property (signature and documentation, as in a .cs file), or null.</summary>
    public static async Task<LspHover?> HoverAsync(
        LanguageSessionService languages, XamlCompletionService.BindingTarget target, CancellationToken cancellationToken)
    {
        if (await SymbolsAsync(languages, target) is not var (client, symbols)
            || symbols.FirstOrDefault(symbol => symbol.Name == target.Property) is not { } declaration)
            return null;
        return await client.HoverAsync(declaration.Uri, declaration.Range.StartLine, declaration.Range.StartChar, cancellationToken);
    }

    /// <summary>The workspace symbols on the binding's type that the property's name finds (Roslyn's search is fuzzy:
    /// "Title" also finds the field "_title").</summary>
    private static async Task<(LspClient Client, List<LspLocation> Symbols)?> SymbolsAsync(
        LanguageSessionService languages, XamlCompletionService.BindingTarget target)
    {
        if (await languages.ClientForLanguageAsync(LanguageIds.CSharp) is not { SupportsWorkspaceSymbols: true } client)
            return null;
        var symbols = (await client.WorkspaceSymbolsAsync(target.Property))
            .Where(symbol => IsOn(symbol.ContainerName, target))
            .ToList();
        return (client, symbols);
    }

    /// <summary>The CommunityToolkit.Mvvm field names that generate <paramref name="property"/>: _title, title, m_title.</summary>
    private static bool IsBackingField(string? name, string property)
    {
        if (name is null || property.Length == 0)
            return false;
        var camel = char.ToLowerInvariant(property[0]) + property[1..];
        return name == "_" + camel || name == camel || name == "m_" + camel;
    }

    /// <summary>
    /// Roslyn names the container with extras ("RunViewModel (project AXamlIDE (net10.0))"); the type is the first word.
    /// </summary>
    private static bool IsOn(string? container, XamlCompletionService.BindingTarget target)
    {
        if (container is null || ContainerType().Match(container) is not { Success: true } match)
            return false;
        var type = match.Groups[1].Value;
        return type == target.Owner || type == target.OwnerFullName || type.EndsWith("." + target.Owner, StringComparison.Ordinal);
    }

    private static bool IsGenerated(string uri)
        => LspHelpers.IsSourceGenerated(uri) || uri.Contains("/obj/", StringComparison.Ordinal)
           || uri.Contains(".g.cs", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?:in\s+)?([\w.]+)")]
    private static partial Regex ContainerType();
}
