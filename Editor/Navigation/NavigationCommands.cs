using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;
using ICSharpCode.Decompiler;
using static AXamlIDE.Services.Lsp.LspHelpers;

namespace AXamlIDE.Editor.Navigation;

/// <summary>
/// The user-invokable navigation commands: Go to Definition, Find References, Rename,
/// Go to Symbol and Quick Open. Reaches the rest of the app only through
/// ctx.Documents (IDocumentHost), ctx.Status (a message) and the dialog service.
/// </summary>
public sealed class NavigationCommands(EditorContext ctx, IDialogService dialogs, WorkspaceEditService edits,
    NavigationService navigator)
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

        var query = await dialogs.PromptForTextAsync("Go to Symbol", "Search types and members...", "Search");
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

    /// <summary>
    /// Rename the C# symbol at the caret and every reference to it (through Roslyn), after showing the changes.
    /// C# only, as in other IDEs: XAML isn't touched (renaming there causes more trouble than it saves); the
    /// next build reports any XAML that still uses the old name.
    /// </summary>
    public async Task RenameSymbolAsync()
    {
        if (ctx.Active is { } active && active.LanguageId != LanguageIds.CSharp)
        {
            ctx.Status("Rename works in C# files; XAML that uses the name is reported by the next build.");
            return;
        }
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsRename)
        {
            ctx.Status("The active language server does not support Rename Symbol.");
            return;
        }

        var current = IdentifierAtCaret(document);
        var newName = await dialogs.PromptForTextAsync(
            current.Length > 0 ? $"Rename '{current}'" : "Rename Symbol", "Enter the new symbol name...", "Rename", current);
        if (string.IsNullOrWhiteSpace(newName) || newName == current)
            return;

        try
        {
            var (line, character) = CurrentLspPosition();
            var edit = await client.RenameAsync(document.Uri, line, character, newName);
            var replacements = ParseWorkspaceEdit(edit);
            foreach (var uri in replacements.Keys.Where(uri => !uri.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList())
                replacements.Remove(uri);   // C# files only
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

    /// <summary>The C# identifier the caret is in (or just after), or "".</summary>
    private string IdentifierAtCaret(OpenDocument document)
    {
        var text = document.Document;
        var caret = Math.Clamp(ctx.Editor.CaretOffset, 0, text.TextLength);
        var start = caret;
        while (start > 0 && IsIdentifierChar(text.GetCharAt(start - 1)))
            start--;
        var end = caret;
        while (end < text.TextLength && IsIdentifierChar(text.GetCharAt(end)))
            end++;
        return text.GetText(start, end - start).TrimStart('@');

        static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';
    }

    private bool TryGetActiveLanguageClient(out OpenDocument document, out LspClient client)
    {
        if (ctx.Active is { } active && ctx.Languages.ConnectionFor(active) is { } connection)
        {
            connection.Sync.Flush();
            document = active;
            client = connection.Client;
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
