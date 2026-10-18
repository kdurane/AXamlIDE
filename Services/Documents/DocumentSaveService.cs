using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// Saving documents the way the user expects: format-on-save (when enabled and the language
/// server can format), read-only documents refused, and failures reported in the status bar.
/// Used by the shell (Ctrl+S, close prompts) and by Run (save all before building).
/// </summary>
public sealed class DocumentSaveService(
    DocumentManager documents,
    LanguageSessionService languages,
    IEditorSettings settings,
    IMessenger messenger)
{
    /// <summary>Save one document. Returns false if it couldn't be saved (and says why in the status bar).</summary>
    public async Task<bool> SaveAsync(OpenDocument document)
    {
        if (document.IsReadOnly)
        {
            Status("Decompiled source is read-only.");
            return false;
        }

        if (settings.FormatOnSave && languages.ClientFor(document)?.SupportsDocumentFormatting == true)
            await languages.FormatDocumentAsync(document, reportUnchanged: false);

        try
        {
            documents.Save(document);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't save '{document.Title}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Save every document with unsaved changes (read-only ones are skipped). False if any failed.</summary>
    public async Task<bool> SaveAllAsync()
    {
        var allSaved = true;
        foreach (var document in documents.Documents.Where(d => d.IsDirty && !d.IsReadOnly).ToArray())
            allSaved &= await SaveAsync(document);
        return allSaved;
    }

    private void Status(string text) => messenger.Send(new StatusMessage(text));
}
