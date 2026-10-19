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
    /// The property's declarations in the workspace (a source-generated half of a partial property only when
    /// there's nothing else), with the client that found them; null when there's no C# server or no match.
    /// </summary>
    public static async Task<(LspClient Client, IReadOnlyList<LspLocation> Declarations)?> FindAsync(
        LanguageSessionService languages, XamlCompletionService.BindingTarget target)
    {
        if (await languages.ClientForLanguageAsync(LanguageIds.CSharp) is not { SupportsWorkspaceSymbols: true } client)
            return null;

        var matches = (await client.WorkspaceSymbolsAsync(target.Property))
            .Where(symbol => symbol.Name == target.Property && IsOn(symbol.ContainerName, target))
            .ToList();
        if (matches.Count == 0)
            return null;

        var written = matches.Where(symbol => !IsGenerated(symbol.Uri)).ToList();
        return (client, written.Count > 0 ? written : matches);
    }

    /// <summary>The C# hover of the bound property (signature and documentation, as in a .cs file), or null.</summary>
    public static async Task<LspHover?> HoverAsync(
        LanguageSessionService languages, XamlCompletionService.BindingTarget target, CancellationToken cancellationToken)
    {
        if (await FindAsync(languages, target) is not ({ } client, [var declaration, ..]))
            return null;
        return await client.HoverAsync(declaration.Uri, declaration.Range.StartLine, declaration.Range.StartChar, cancellationToken);
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
        => uri.Contains("/obj/", StringComparison.Ordinal) || uri.Contains(".g.cs", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?:in\s+)?([\w.]+)")]
    private static partial Regex ContainerType();
}
