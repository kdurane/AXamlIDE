using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Commands;

/// <summary>
/// Commands on documents that work the same from any window: they act on the given document, or on
/// <see cref="DocumentManager.Active"/> (the selected tab, wherever it is) when the parameter is null.
/// Used by menus, the KeyMap and every tab strip.
/// </summary>
public sealed partial class DocumentCommands(
    DocumentManager documents,
    DocumentSaveService saver,
    LanguageSessionService languages,
    IDialogService dialogs,
    IMessenger messenger)
{
    [RelayCommand]
    private Task SaveDocumentAsync(OpenDocument? document)
        => (document ?? documents.Active) is { } target ? saver.SaveAsync(target) : Task.CompletedTask;

    /// <summary>File > Save All (Ctrl+Shift+S): every document with unsaved changes.</summary>
    [RelayCommand]
    private async Task SaveAllAsync()
    {
        var dirty = documents.Documents.Count(d => d.IsDirty && !d.IsReadOnly);
        if (dirty == 0)
        {
            messenger.Send(new StatusMessage("No unsaved changes."));
            return;
        }
        if (await saver.SaveAllAsync())
            messenger.Send(new StatusMessage(dirty == 1 ? "1 file saved." : $"{dirty} files saved."));
    }

    /// <summary>
    /// File > Save As...: write the document's text to a new file, and carry on editing that file in the same tab
    /// (the original is left as it is on disk, as in Visual Studio).
    /// </summary>
    [RelayCommand]
    private async Task SaveDocumentAsAsync(OpenDocument? document)
    {
        if ((document ?? documents.Active) is not { } target)
            return;
        if (await dialogs.PickSaveFileAsync(target.Path) is not { } path)
            return;
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(target.Path), StringComparison.OrdinalIgnoreCase))
        {
            await saver.SaveAsync(target);   // the same file: a plain save
            return;
        }

        try
        {
            File.WriteAllText(path, target.Document.Text, target.Encoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messenger.Send(new StatusMessage($"Couldn't save '{Path.GetFileName(path)}': {ex.Message}"));
            return;
        }

        // Carry on in the new file: its tab takes the old one's place (the text is identical, so nothing is lost).
        documents.Close(target, force: true);
        documents.Active = documents.Open(path);
        messenger.Send(new StatusMessage($"Saved as {Path.GetFileName(path)}."));
    }

    /// <summary>
    /// Close a tab, asking about unsaved changes first. The tab to its right (or the new last tab)
    /// becomes active if the closed tab was the active one.
    /// </summary>
    [RelayCommand]
    private async Task CloseDocumentAsync(OpenDocument? document)
    {
        document ??= documents.Active;
        if (document is null)
            return;

        var index = documents.Documents.IndexOf(document);
        var wasActive = ReferenceEquals(documents.Active, document);
        if (!await CloseAsync(document))
            return;

        // Only when the active tab closed: with a diff in front (no active document), closing a background tab
        // mustn't bring another editor to the front.
        var open = documents.Documents;
        if (wasActive && open.Count > 0 && documents.Active is null)
            documents.Active = open[Math.Min(index, open.Count - 1)];
    }

    /// <summary>Window > Close All Documents: every tab, asking about each with unsaved changes (Cancel keeps them all).</summary>
    [RelayCommand]
    private Task CloseAllAsync() => CloseManyAsync(documents.Documents.ToArray());

    /// <summary>The tab's menu: Close All But This.</summary>
    [RelayCommand]
    private Task CloseOthersAsync(OpenDocument? keep)
    {
        keep ??= documents.Active;
        return CloseManyAsync(documents.Documents.Where(d => !ReferenceEquals(d, keep)).ToArray());
    }

    /// <summary>
    /// Close these documents, asking about every unsaved one first; nothing is closed (and false comes back)
    /// if the user cancels or a save fails. Used by a floating window's close button.
    /// </summary>
    public Task<bool> CloseDocumentsAsync(IReadOnlyList<OpenDocument> targets) => CloseManyAsync(targets);

    private async Task<bool> CloseManyAsync(IReadOnlyList<OpenDocument> targets)
    {
        // One question for all the unsaved ones, as Visual Studio asks.
        var dirty = targets.Where(d => d.IsDirty).ToArray();
        var choice = dirty.Length == 0 ? SaveChoice.Discard : await dialogs.AskSaveAsync(dirty);
        if (choice == SaveChoice.Cancel)
            return false;

        if (choice == SaveChoice.Save)
            foreach (var document in dirty)
                if (!await saver.SaveAsync(document))
                    return false;   // a save failed: keep everything open

        var active = documents.Active;
        foreach (var document in targets)
            documents.Close(document, force: true);

        if (active is not null && documents.Active is null && documents.Documents.Count > 0)
            documents.Active = documents.Documents.Contains(active) ? active : documents.Documents[^1];
        return true;
    }

    private async Task<bool> CloseAsync(OpenDocument document)
    {
        if (document.IsDirty)
        {
            switch (await dialogs.AskSaveAsync(document))
            {
                case SaveChoice.Cancel: return false;
                case SaveChoice.Save:
                    if (!await saver.SaveAsync(document)) return false;   // keep the tab if the save failed
                    break;
            }
        }

        documents.Close(document, force: true);
        return true;
    }

    [RelayCommand]
    private async Task FormatDocumentAsync(OpenDocument? document)
    {
        if ((document ?? documents.Active) is not { } target)
        {
            messenger.Send(new StatusMessage("Open a document to format it."));
            return;
        }

        await languages.FormatDocumentAsync(target, reportUnchanged: true);
    }
}
