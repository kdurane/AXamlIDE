using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Editor.CodeCompletion;

/// <summary>
/// Keeps AXAML binding completion up to date with the view models' C# source, as Rider does. The AXAML engine reads
/// the built DLL, so a property added since the last build was missing from {Binding ...}; above all the ones the
/// CommunityToolkit.Mvvm generators make ([ObservableProperty] Title, [RelayCommand] SaveCommand). This asks the C#
/// server for the properties of each x:DataType type, in every file the type is in (the generated ones too), and hands
/// them to <see cref="XamlLanguageService.SetSourceProperties"/>.
/// </summary>
public sealed class XamlSourceMembers(LanguageSessionService languages, XamlLanguageService xaml)
{
    // LSP SymbolKind
    private const int ClassKind = 5, PropertyKind = 7, StructKind = 23;

    /// <summary>Asked again only after this long: completion opens on most keys in AXAML.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, (DateTime At, Task Update)> _updates = new(StringComparer.Ordinal);

    /// <summary>
    /// Bring the document's data types up to date, waiting at most <paramref name="wait"/> (a slow server just means
    /// this list misses them; the next one has them).
    /// </summary>
    public async Task UpdateAsync(string axamlText, TimeSpan wait)
    {
        var pending = new List<Task>();
        foreach (var fullName in XamlTypeReference.DataTypes(axamlText))
        {
            // One question at a time per type: a busy server doesn't get a queue of them.
            if (!_updates.TryGetValue(fullName, out var last) || last.Update.IsCompleted && DateTime.UtcNow - last.At > Fresh)
                _updates[fullName] = last = (DateTime.UtcNow, FetchAsync(fullName));
            if (!last.Update.IsCompleted)
                pending.Add(last.Update);
        }
        if (pending.Count > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(wait));
    }

    private async Task FetchAsync(string fullName)
    {
        try
        {
            // Only a server that's already up: AXAML completion doesn't start (and wait for) one.
            var starting = languages.ClientForLanguageAsync(LanguageIds.CSharp);
            if (!starting.IsCompletedSuccessfully
                || starting.Result is not { SupportsWorkspaceSymbols: true, SupportsDocumentSymbols: true } client)
                return;
            var name = fullName[(fullName.LastIndexOf('.') + 1)..];
            // Each part of a partial class is a symbol of its own: the user's file, and each generator's .g.cs.
            var parts = (await client.WorkspaceSymbolsAsync(name))
                .Where(symbol => symbol.Name == name && symbol.Kind is ClassKind or StructKind)
                .Select(symbol => symbol.Uri)
                .Distinct()
                .ToList();
            var properties = new List<string>();
            foreach (var uri in parts)
                foreach (var symbol in await client.DocumentSymbolsAsync(uri))
                    if (symbol.Kind == PropertyKind && symbol.ContainerName == fullName && !properties.Contains(symbol.Name))
                        properties.Add(symbol.Name);
            if (properties.Count > 0)
                xaml.SetSourceProperties(fullName, properties);
        }
        catch (Exception)
        {
            // Server busy, restarting or gone: the built DLL's properties still show.
        }
    }
}
