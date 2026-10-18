using System;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels.Documents;

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
        if (document.IsDirty)
        {
            switch (await dialogs.AskSaveAsync(document))
            {
                case SaveChoice.Cancel: return;
                case SaveChoice.Save:
                    if (!await saver.SaveAsync(document)) return;   // keep the tab if the save failed
                    break;
            }
        }

        documents.Close(document, force: true);

        var open = documents.Documents;
        if (open.Count > 0 && documents.Active is null)
            documents.Active = open[Math.Min(index, open.Count - 1)];
    }

    [RelayCommand]
    private async Task FormatDocumentAsync(OpenDocument? document)
    {
        if ((document ?? documents.Active) is not { } target)
        {
            messenger.Send(new StatusMessage("Open a document with an active language server to format it."));
            return;
        }

        await languages.FormatDocumentAsync(target, reportUnchanged: true);
    }
}
