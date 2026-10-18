using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using ICSharpCode.Decompiler;
using static AXamlIDE.Services.Lsp.LspHelpers;

namespace AXamlIDE.Views.Editor.Navigation;

/// <summary>
/// The user-invokable navigation commands: Go to Definition, Find References, Rename,
/// Go to Symbol and Quick Open. Reaches the rest of the app only through
/// ctx.Documents (IDocumentHost), ctx.Status (a message) and the dialog service.
/// </summary>
public sealed class NavigationCommands(EditorContext ctx, IDialogService dialogs, WorkspaceEditService edits,
    EditorNavigator navigator)
{
    // ------------------------------------------------------------ quick open

    public async Task QuickOpenAsync()
    {
        try
        {
            var files = await ctx.Documents.GetWorkspaceFilesAsync();
            if (files.Count == 0)
            {
                ctx.Status("No workspace files are available to open.");
                return;
            }

            var selectedPath = await dialogs.ShowQuickOpenDialogAsync(files);
            if (selectedPath is null)
                return;

            ctx.Documents.OpenAndActivate(selectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ctx.Status($"Quick Open failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------- go to symbol

    public async Task SearchWorkspaceSymbolsAsync()
    {
        if (!TryGetActiveLanguageClient(out _, out var client))
            return;
        if (!client.SupportsWorkspaceSymbols)
        {
            ctx.Status("The active language server does not support workspace symbols.");
            return;
        }

        var query = await dialogs.PromptForTextAsync("Go to Symbol", "Search types and members...");
        if (string.IsNullOrWhiteSpace(query))
            return;

        try
        {
            var symbols = await client.WorkspaceSymbolsAsync(query);
            if (symbols.Count == 0)
            {
                ctx.Status("No matching workspace symbols.");
                return;
            }

            var selected = await dialogs.PickLocationAsync(
                "Workspace Symbols",
                symbols,
                symbol => string.IsNullOrEmpty(symbol.ContainerName)
                    ? symbol.Name ?? "(unnamed symbol)"
                    : $"{symbol.Name}  —  {symbol.ContainerName}");
            if (selected is not null)
                navigator.ToLocation(selected);
        }
        catch (Exception ex)
        {
            ctx.Status($"Workspace symbol search failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------ go to definition

    public async Task GoToDefinitionAsync()
    {
        if (ctx.Active is { } active
            && active.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            await GoToXamlDefinitionAsync(active);
            return;
        }

        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsDefinition)
        {
            ctx.Status("The active language server does not support Go to Definition.");
            return;
        }

        try
        {
            var (line, character) = CurrentLspPosition();
            var locations = await client.DefinitionAsync(document.Uri, line, character);
            await NavigateToLocationsAsync("Go to Definition", locations);
        }
        catch (Exception ex)
        {
            ctx.Status($"Go to Definition failed: {ex.Message}");
        }
    }

    private async Task GoToXamlDefinitionAsync(OpenDocument document)
    {
        try
        {
            var source = await ctx.Xaml.DecompileTypeAsync(
                document.Path, document.Document.Text, ctx.Editor.CaretOffset);
            if (source is null)
            {
                ctx.Status("No resolvable AXAML type at the caret.");
                return;
            }

            var decompiledDocument = ctx.Documents.OpenReadOnly(source.Path, source.Text, LanguageIds.CSharp);
            ctx.Documents.Activate(decompiledDocument);
            ctx.Status($"Opened decompiled source for {source.TypeFullName} (read-only).");
        }
        catch (Exception ex) when (ex is DecompilerException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException
                                       or BadImageFormatException or NotSupportedException)
        {
            ctx.Status($"AXAML type navigation failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------- find references

    public async Task FindReferencesAsync()
    {
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsReferences)
        {
            ctx.Status("The active language server does not support Find All References.");
            return;
        }

        try
        {
            var (line, character) = CurrentLspPosition();
            var locations = await client.ReferencesAsync(document.Uri, line, character);
            await NavigateToLocationsAsync("Find All References", locations);
        }
        catch (Exception ex)
        {
            ctx.Status($"Find All References failed: {ex.Message}");
        }
    }

    private async Task NavigateToLocationsAsync(string title, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
        {
            ctx.Status($"No results for {title}.");
            return;
        }

        var selected = locations.Count == 1
            ? locations[0]
            : await dialogs.PickLocationAsync(title, locations, FormatLocation);
        if (selected is not null)
            navigator.ToLocation(selected);
    }

    // ---------------------------------------------------------------- rename

    public async Task RenameSymbolAsync()
    {
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsRename)
        {
            ctx.Status("The active language server does not support Rename Symbol.");
            return;
        }

        var newName = await dialogs.PromptForTextAsync("Rename Symbol", "Enter the new symbol name...");
        if (string.IsNullOrWhiteSpace(newName))
            return;

        try
        {
            var (line, character) = CurrentLspPosition();
            var edit = await client.RenameAsync(document.Uri, line, character, newName);
            var replacements = ParseWorkspaceEdit(edit);
            if (replacements.Count == 0)
            {
                ctx.Status("The language server returned no rename changes.");
                return;
            }

            if (!await dialogs.ConfirmRenameAsync(replacements))
                return;

            edits.Apply(replacements);
            ctx.Status($"Applied rename changes in {replacements.Count} file(s). Review and save the modified documents.");
        }
        catch (Exception ex)
        {
            ctx.Status($"Rename failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- helpers

    private bool TryGetActiveLanguageClient(out OpenDocument document, out LspClient client)
    {
        if (ctx.Active is { Client: not null, Sync: not null } active)
        {
            active.Sync.Flush();
            document = active;
            client = active.Client;
            return true;
        }

        document = null!;
        client = null!;
        ctx.Status("Open a supported source document and wait for its language server before using symbol navigation.");
        return false;
    }

    private (int Line, int Character) CurrentLspPosition()
    {
        var editor = ctx.Editor;
        var location = editor.Document.GetLocation(editor.CaretOffset);
        return (location.Line - 1, location.Column - 1);
    }
}
