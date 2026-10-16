using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AXaxmlIDE.Helpers;
using AXaxmlIDE.Lsp;
using AXaxmlIDE.Models;
using AXaxmlIDE.Services;
using AXaxmlIDE.ViewModels;
using ICSharpCode.Decompiler;
using static AXaxmlIDE.Helpers.LspHelpers;

namespace AXaxmlIDE.EditorFeatures;

/// <summary>
/// The user-invokable navigation commands: Go to Definition, Find References, Rename,
/// Go to Symbol and Quick Open. Replaces the matching block in MainWindow. Bodies are
/// copy-paste with these swaps:
///   _active                  -> _ctx.Active
///   ViewModel.X              -> _ctx.ViewModel.X   (status messages -> _ctx.Status)
///   PromptForTextAsync etc.  -> _dialogs.*
///   ApplyWorkspaceEdits      -> _edits.Apply
///   NavigateToLocationAsync  -> _navigator.ToLocation
///   DocTabs.SelectedItem = ViewModel.OpenDocument(p) -> _ctx.OpenAndActivate(p)
/// </summary>
public sealed class NavigationCommands
{
    private readonly EditorContext _ctx;
    private readonly DialogService _dialogs;
    private readonly WorkspaceEditService _edits;
    private readonly EditorNavigator _navigator;

    public NavigationCommands(
        EditorContext ctx,
        DialogService dialogs,
        WorkspaceEditService edits,
        EditorNavigator navigator)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        _edits = edits;
        _navigator = navigator;
    }

    // ------------------------------------------------------------ quick open

    public async Task QuickOpenAsync()
    {
        try
        {
            var files = await _ctx.ViewModel.GetWorkspaceFilesAsync();
            if (files.Count == 0)
            {
                _ctx.Status("No workspace files are available to open.");
                return;
            }

            var selectedPath = await _dialogs.ShowQuickOpenDialogAsync(files);
            if (selectedPath is null)
                return;

            _ctx.OpenAndActivate(selectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ctx.Status($"Quick Open failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------- go to symbol

    public async Task SearchWorkspaceSymbolsAsync()
    {
        if (!TryGetActiveLanguageClient(out _, out var client))
            return;
        if (!client.SupportsWorkspaceSymbols)
        {
            _ctx.Status("The active language server does not support workspace symbols.");
            return;
        }

        var query = await _dialogs.PromptForTextAsync("Go to Symbol", "Search types and members...");
        if (string.IsNullOrWhiteSpace(query))
            return;

        try
        {
            var symbols = await client.WorkspaceSymbolsAsync(query);
            if (symbols.Count == 0)
            {
                _ctx.Status("No matching workspace symbols.");
                return;
            }

            var selected = await _dialogs.PickLocationAsync(
                "Workspace Symbols",
                symbols,
                symbol => string.IsNullOrEmpty(symbol.ContainerName)
                    ? symbol.Name ?? "(unnamed symbol)"
                    : $"{symbol.Name}  —  {symbol.ContainerName}");
            if (selected is not null)
                _navigator.ToLocation(selected);
        }
        catch (Exception ex)
        {
            _ctx.Status($"Workspace symbol search failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------ go to definition

    public async Task GoToDefinitionAsync()
    {
        if (_ctx.Active is { } active
            && active.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            await GoToXamlDefinitionAsync(active);
            return;
        }

        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsDefinition)
        {
            _ctx.Status("The active language server does not support Go to Definition.");
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
            _ctx.Status($"Go to Definition failed: {ex.Message}");
        }
    }

    private async Task GoToXamlDefinitionAsync(OpenDocument document)
    {
        try
        {
            var source = await _ctx.ViewModel.DecompileXamlTypeAsync(
                document.Path, document.Document.Text, _ctx.Editor.CaretOffset);
            if (source is null)
            {
                _ctx.Status("No resolvable AXAML type at the caret.");
                return;
            }

            var decompiledDocument = _ctx.ViewModel.OpenReadOnlyDocument(source.Path, source.Text, "csharp");
            _ctx.ActivateDocument(decompiledDocument);
            _ctx.Status($"Opened decompiled source for {source.TypeFullName} (read-only).");
        }
        catch (Exception ex) when (ex is DecompilerException or IOException or UnauthorizedAccessException
                                   or InvalidOperationException or ArgumentException
                                   or BadImageFormatException or NotSupportedException)
        {
            _ctx.Status($"AXAML type navigation failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------- find references

    public async Task FindReferencesAsync()
    {
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsReferences)
        {
            _ctx.Status("The active language server does not support Find All References.");
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
            _ctx.Status($"Find All References failed: {ex.Message}");
        }
    }

    private async Task NavigateToLocationsAsync(string title, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
        {
            _ctx.Status($"No results for {title}.");
            return;
        }

        var selected = locations.Count == 1
            ? locations[0]
            : await _dialogs.PickLocationAsync(title, locations, FormatLocation);
        if (selected is not null)
            _navigator.ToLocation(selected);
    }

    // ---------------------------------------------------------------- rename

    public async Task RenameSymbolAsync()
    {
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsRename)
        {
            _ctx.Status("The active language server does not support Rename Symbol.");
            return;
        }

        var newName = await _dialogs.PromptForTextAsync("Rename Symbol", "Enter the new symbol name...");
        if (string.IsNullOrWhiteSpace(newName))
            return;

        try
        {
            var (line, character) = CurrentLspPosition();
            var edit = await client.RenameAsync(document.Uri, line, character, newName);
            var replacements = ParseWorkspaceEdit(edit);
            if (replacements.Count == 0)
            {
                _ctx.Status("The language server returned no rename changes.");
                return;
            }

            if (!await _dialogs.ConfirmRenameAsync(replacements))
                return;

            _edits.Apply(replacements);
            _ctx.Status($"Applied rename changes in {replacements.Count} file(s). Review and save the modified documents.");
        }
        catch (Exception ex)
        {
            _ctx.Status($"Rename failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- helpers

    private bool TryGetActiveLanguageClient(out OpenDocument document, out LspClient client)
    {
        if (_ctx.Active is { Client: not null, Sync: not null } active)
        {
            active.Sync.Flush();
            document = active;
            client = active.Client;
            return true;
        }

        document = null!;
        client = null!;
        _ctx.Status("Open a supported source document and wait for its language server before using symbol navigation.");
        return false;
    }

    private (int Line, int Character) CurrentLspPosition()
    {
        var editor = _ctx.Editor;
        var location = editor.Document.GetLocation(editor.CaretOffset);
        return (location.Line - 1, location.Column - 1);
    }
}
