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

    public LanguageSessionService(DocumentManager documents, LspManager servers, IMessenger messenger)
    {
        _documents = documents;
        _servers = servers;
        _messenger = messenger;

        _documents.Opened += OnDocumentOpened;
        _documents.Closing += OnDocumentClosing;
        _documents.Saved += OnDocumentSaved;

        _servers.StatusChanged += OnServerStatusChanged;
        _servers.DiagnosticsPublished += OnDiagnosticsPublished;
        _servers.DiagnosticsRefreshNeeded += OnDiagnosticsRefreshNeeded;
        _servers.WorkspaceEditHandler = edit => WorkspaceEditRequested?.Invoke(edit) ?? Task.FromResult(false);
    }

    /// <summary>Raised once a document is connected to its server (it now has Client and Sync).</summary>
    public event Action<OpenDocument>? ClientAttached;

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
        foreach (var document in _documents.Documents)
        {
            document.Sync?.Dispose();
            document.Sync = null;
            document.Client = null;
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
                           || !_documents.Documents.Contains(document) || document.Sync is not null)
            return;

        document.Client = client;
        client.DidOpen(document.Uri, document.LanguageId, 1, document.Document.Text);
        document.Sync = new DocumentSync(document.Document, client, document.Uri);
        ClientAttached?.Invoke(document);
        document.Sync.Flushed += () =>
        {
            if (client.SupportsPullDiagnostics)
                _ = PullDiagnosticsAsync(document);
        };

        if (client.SupportsPullDiagnostics)
            _ = PullDiagnosticsAsync(document);
    }

    private void OnDocumentClosing(OpenDocument document)
    {
        if (document.Sync is not null)
            document.Client?.DidClose(document.Uri);
    }

    private void OnDocumentSaved(OpenDocument document)
    {
        if (document.Sync is not null)
            document.Client?.DidSave(document.Uri, document.Document.Text);
    }

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
        foreach (var document in _documents.Documents.Where(document => ReferenceEquals(document.Client, client)))
            _ = PullDiagnosticsAsync(document);
    }

    private async Task PullDiagnosticsAsync(OpenDocument document)
    {
        if (document.Client is not { SupportsPullDiagnostics: true } client
            || document.Sync is not { } sync)
            return;

        document.PullCts?.Cancel();
        var cancellation = document.PullCts = new CancellationTokenSource();
        var version = sync.Version;

        try
        {
            var diagnostics = await client.PullDiagnosticsAsync(document.Uri, cancellation.Token);
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
        if (document.ApplyDiagnostics(diagnostics, version))
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
        if (document.Client is not { } client || document.Sync is not { } sync)
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
