using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// Connects open documents to their language servers: didOpen/didChange/didSave/didClose,
/// diagnostics (pushed and pulled), formatting, and restarting the servers.
///
/// It watches <see cref="DocumentManager"/> by itself, so nobody has to tell it when a file
/// opens or closes. Results go out as messages: <see cref="DiagnosticsChangedMessage"/> and
/// <see cref="StatusMessage"/> (server status, formatting results).
/// </summary>
public sealed class LanguageSessionService
{
    private const string IdleStatus = "language servers: idle";

    private readonly DocumentManager _documents;
    private readonly LspManager _servers;
    private readonly IMessenger _messenger;
    private readonly Dictionary<OpenDocument, DocumentConnection> _connections = [];

    public LanguageSessionService(DocumentManager documents, LspManager servers, IEditorSettings settings, IMessenger messenger)
    {
        _documents = documents;
        _servers = servers;
        _messenger = messenger;

        // Some settings are server options (Roslyn's inlay hints, its completion of unimported types).
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEditorSettings.Features))
                _servers.SettingsChanged();
        };

        _documents.Opened += OnDocumentOpened;
        _documents.Closing += OnDocumentClosing;
        _documents.Saving += OnDocumentSaving;
        _documents.Saved += OnDocumentSaved;

        _servers.StatusChanged += OnServerStatusChanged;
        _servers.DiagnosticsPublished += OnDiagnosticsPublished;
        _servers.DiagnosticsRefreshNeeded += OnDiagnosticsRefreshNeeded;
        _servers.WorkspaceEditHandler = edit => WorkspaceEditRequested?.Invoke(edit) ?? Task.FromResult(false);
    }

    /// <summary>Raised once a document is connected to its server (<see cref="ConnectionFor"/> is non-null from then on).</summary>
    public event Action<OpenDocument>? ClientAttached;

    /// <summary>The document's language-server connection, or null if it has none (yet, or for its language).</summary>
    public DocumentConnection? ConnectionFor(OpenDocument? document)
        => document is not null && _connections.TryGetValue(document, out var connection) ? connection : null;

    /// <summary>Shorthand for <c>ConnectionFor(document)?.Client</c>.</summary>
    public LspClient? ClientFor(OpenDocument? document) => ConnectionFor(document)?.Client;

    /// <summary>The server for a language (starting it if needed), without needing a document of it open.</summary>
    public Task<LspClient?> ClientForLanguageAsync(string languageId) => _servers.GetClientAsync(languageId);

    /// <summary>
    /// Applies an edit the server pushed (workspace/applyEdit). Set by the editor, which owns
    /// text editing. A delegate rather than a message because the server needs the result.
    /// </summary>
    public Func<JsonElement, Task<bool>>? WorkspaceEditRequested { get; set; }

    // ---------------------------------------------------------------- workspace

    /// <summary>A folder or solution was opened: (re)start servers rooted there and reattach documents.</summary>
    public void SetWorkspace(LspWorkspace workspace)
    {
        _servers.SetWorkspace(workspace);
        ReattachDocuments();
    }

    public void ClearWorkspace() => _servers.ClearWorkspace();

    public void Restart()
    {
        _servers.Restart();
        ReattachDocuments();
    }

    private void ReattachDocuments()
    {
        foreach (var connection in _connections.Values)
            connection.Dispose();
        _connections.Clear();

        foreach (var document in _documents.Documents)
        {
            document.ClearDiagnostics();
            _messenger.Send(new DiagnosticsChangedMessage(document));
            if (_servers.Handles(document.LanguageId))
                _ = AttachAsync(document);
        }
    }

    // ---------------------------------------------------------- document lifecycle

    private void OnDocumentOpened(OpenDocument document)
    {
        if (!document.IsReadOnly && _servers.Handles(document.LanguageId))
            _ = AttachAsync(document);
    }

    private async Task AttachAsync(OpenDocument document)
    {
        var generation = _servers.Generation;
        var client = await _servers.GetClientAsync(document.LanguageId);
        if (client is null || generation != _servers.Generation
                           || !_documents.Documents.Contains(document) || _connections.ContainsKey(document))
            return;

        client.DidOpen(document.Uri, document.LanguageId, 1, document.Document.Text);
        var connection = new DocumentConnection(client, new DocumentSync(document.Document, client, document.Uri));
        _connections[document] = connection;
        ClientAttached?.Invoke(document);
        connection.Sync.Flushed += () =>
        {
            if (client.SupportsPullDiagnostics)
                _ = PullDiagnosticsAsync(document);
        };

        if (client.SupportsPullDiagnostics)
            _ = PullDiagnosticsAsync(document);
    }

    private void OnDocumentClosing(OpenDocument document)
    {
        if (!_connections.Remove(document, out var connection))
            return;
        connection.Client.DidClose(document.Uri);
        connection.Dispose();
    }

    // The server has to have the latest text before it's told about the save.
    private void OnDocumentSaving(OpenDocument document) => ConnectionFor(document)?.Sync.Flush();

    private void OnDocumentSaved(OpenDocument document)
        => ConnectionFor(document)?.Client.DidSave(document.Uri, document.Document.Text);

    // ---------------------------------------------------------------- diagnostics

    private void OnDiagnosticsPublished(string uri, IReadOnlyList<LspDiagnostic> diagnostics, int? version)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_documents.ByUri(uri) is { } document)
                ApplyDiagnostics(document, diagnostics, version);
        });
    }

    private void OnDiagnosticsRefreshNeeded(LspClient client)
    {
        foreach (var (document, connection) in _connections.ToArray())
            if (ReferenceEquals(connection.Client, client))
                _ = PullDiagnosticsAsync(document);
    }

    private async Task PullDiagnosticsAsync(OpenDocument document)
    {
        if (ConnectionFor(document) is not { Client.SupportsPullDiagnostics: true } connection)
            return;

        connection.PullCts?.Cancel();
        var cancellation = connection.PullCts = new CancellationTokenSource();
        var version = connection.Sync.Version;

        try
        {
            var diagnostics = await connection.Client.PullDiagnosticsAsync(document.Uri, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                ApplyDiagnostics(document, diagnostics, version);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to pull diagnostics for '{document.Path}': {ex}");
        }
    }

    private void ApplyDiagnostics(OpenDocument document, IReadOnlyList<LspDiagnostic> diagnostics, int? version)
    {
        // Stale: the user has typed since the server analysed, so its offsets would be wrong.
        // The server will publish again after it has seen the newer text.
        if (ConnectionFor(document)?.Sync is { } sync
            && (sync.HasPendingChanges || version is { } v && v != sync.Version))
            return;

        document.ApplyDiagnostics(diagnostics);
        _messenger.Send(new DiagnosticsChangedMessage(document));
    }

    private void OnServerStatusChanged()
    {
        var summary = _servers.StatusSummary;
        Status(summary.Length > 0 ? summary : IdleStatus);
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>
    /// Formats the document through its language server. With <paramref name="reportUnchanged"/>
    /// false (format-on-save) it stays quiet unless something goes wrong.
    /// </summary>
    public async Task<bool> FormatDocumentAsync(OpenDocument document, bool reportUnchanged)
    {
        if (ConnectionFor(document) is not { Client: var client, Sync: var sync })
        {
            if (reportUnchanged)
                Status("Open a document with an active language server to format it.");
            return false;
        }

        if (!client.SupportsDocumentFormatting)
        {
            if (reportUnchanged)
                Status("The active language server does not support document formatting.");
            return false;
        }

        try
        {
            sync.Flush();
            var edits = await client.FormattingAsync(document.Uri, tabSize: 4, insertSpaces: true);
            if (edits.Count == 0)
            {
                if (reportUnchanged)
                    Status("The language server returned no formatting changes.");
                return true;
            }

            ApplyTextEdits(document, edits.Select(edit => (edit.Range, edit.NewText)));
            if (reportUnchanged)
                Status("Document formatted. Review and save the changes.");
            return true;
        }
        catch (Exception ex)
        {
            Status($"Document formatting failed: {ex.Message}");
            return false;
        }
    }

    private static void ApplyTextEdits(
        OpenDocument document,
        IEnumerable<(LspRange Range, string NewText)> textEdits)
    {
        if (document.IsReadOnly)
            throw new InvalidOperationException($"Cannot edit read-only document '{document.Path}'.");

        var offsets = textEdits.Select(edit =>
        {
            var start = document.OffsetOf(edit.Range.StartLine, edit.Range.StartChar);
            var end = document.OffsetOf(edit.Range.EndLine, edit.Range.EndChar);
            if (end < start)
                throw new InvalidOperationException(
                    $"The language server returned an invalid edit range for '{document.Path}'.");
            return (Start: start, End: end, edit.NewText);
        }).OrderByDescending(edit => edit.Start).ToList();

        for (var index = 1; index < offsets.Count; index++)
            if (offsets[index - 1].Start < offsets[index].End)
                throw new InvalidOperationException(
                    $"The language server returned overlapping edits for '{document.Path}'.");

        foreach (var (start, end, newText) in offsets)
            document.Document.Replace(start, end - start, newText);
    }

    private void Status(string text) => _messenger.Send(new StatusMessage(text));
}
