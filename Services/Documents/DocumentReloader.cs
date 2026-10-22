using System;
using System.Collections.Generic;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// Keeps open tabs in step with their files on disk: reloads a tab without unsaved edits when its file changes,
/// and flags a tab that has edits (or whose file was deleted) for the user to decide.
/// </summary>
public sealed class DocumentReloader
{
    private readonly Dictionary<OpenDocument, IDisposable> _subscriptions = [];

    public DocumentReloader(DocumentManager documents, FileChangeWatcher watcher)
    {
        documents.Opened += document =>
        {
            if (document.IsReadOnly)
                return;   // decompiled sources and the like: nothing on disk to follow
            _subscriptions[document] = watcher.Watch(document.Path, change => OnChange(document, change));
        };
        documents.Closing += document =>
        {
            if (_subscriptions.Remove(document, out var subscription))
                subscription.Dispose();
        };
    }

    private static void OnChange(OpenDocument document, FileChange change)
    {
        if (change == FileChange.Deleted)
            document.MarkDeletedOnDisk();
        else if (document.IsDirty)
            document.MarkChangedOnDisk();   // don't throw away their edits: the tab asks
        else
            document.ReloadFromDisk();
    }
}
