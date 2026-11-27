using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// Connects open documents to their language servers: didOpen/didChange/didSave/didClose,
/// diagnostics (pushed and pulled), formatting, and restarting the servers.
///
/// It watches <see cref="DocumentManager"/> by itself, so nobody has to tell it when a file
/// opens or closes. Results go out as messages: <see cref="DiagnosticsChangedMessage"/> and
/// <see cref="StatusMessage"/> (formatting results). The servers' own state is shown by the status bar, which reads
/// <see cref="LspManager.Summary"/>.
/// </summary>
public sealed class LanguageSessionService
{
    private readonly DocumentManager _documents;
    private readonly LspManager _servers;
    private readonly IMessenger _messenger;
    private readonly IEditorSettings _settings;
    private readonly Dictionary<OpenDocument, DocumentConnection> _connections = [];

    /// <summary>
    /// A pause in typing C#: Roslyn reruns the source generators (see <see cref="LspClient.RefreshSourceGenerators"/>),
    /// so a new [RelayCommand] method's SaveCommand is in the completion list without saving first, as in Rider.
    /// </summary>
    private readonly DispatcherTimer _generatorRefresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private LspClient? _generatorClient;

    /// <summary>The open folder/solution: where the XAML formatter stops looking for Settings.XamlStyler.</summary>
    private string? _workspaceRoot;

    public LanguageSessionService(DocumentManager documents, LspManager servers, IEditorSettings settings, IMessenger messenger)
    {
        _documents = documents;
        _servers = servers;
        _settings = settings;
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

        _servers.DiagnosticsPublished += OnDiagnosticsPublished;
        _servers.DiagnosticsRefreshNeeded += OnDiagnosticsRefreshNeeded;
        _servers.WorkspaceEditHandler = edit => WorkspaceEditRequested?.Invoke(edit) ?? Task.FromResult(false);

        _generatorRefresh.Tick += (_, _) =>
        {
            _generatorRefresh.Stop();
            _generatorClient?.RefreshSourceGenerators();
            _generatorClient = null;
        };
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
        _workspaceRoot = workspace.RootPath;
        _servers.SetWorkspace(workspace);
        ReattachDocuments();
    }

    public void ClearWorkspace()
    {
        _workspaceRoot = null;
        _servers.ClearWorkspace();
    }

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
            // A file no server handles has no server diagnostics to clear; AXAML's are XamlAnalysisService's own.
            if (!_servers.Handles(document.LanguageId))
                continue;
            document.ClearDiagnostics();
            _messenger.Send(new DiagnosticsChangedMessage(document));
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
            if (document.LanguageId == LanguageIds.CSharp)
            {
                _generatorClient = client;
                _generatorRefresh.Stop();
                _generatorRefresh.Start();   // restart: once typing pauses
            }
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

    // ---------------------------------------------------------------- formatting

    /// <summary>Whether format-on-save should format this document (when format-on-save is on).</summary>
    public bool FormatsOnSave(OpenDocument document)
        => document.LanguageId == LanguageIds.Axaml
            ? _settings.Features.FormatXamlOnSave
            : ClientFor(document)?.SupportsDocumentFormatting == true;

    /// <summary>
    /// Formats the document: AXAML/XAML with the built-in XAML Styler, everything else through its
    /// language server. With <paramref name="reportUnchanged"/> false (format-on-save) it stays quiet
    /// unless something goes wrong.
    /// </summary>
    public async Task<bool> FormatDocumentAsync(OpenDocument document, bool reportUnchanged)
    {
        if (document.LanguageId == LanguageIds.Axaml)
            return FormatXaml(document, reportUnchanged);

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

    /// <summary>
    /// <paramref name="character"/> was just typed, leaving the caret at <paramref name="caret"/>: let the language
    /// server format what it completed ('}' its block, ';' its statement), as Visual Studio does, in one undo step
    /// of its own (so Ctrl+Z takes the formatting back and leaves the character). Quiet: nothing happens when the
    /// server doesn't do it for that character, has nothing to change, or the text changed while it was asked.
    /// </summary>
    public async Task FormatOnTypeAsync(OpenDocument document, int caret, string character)
    {
        if (document.IsReadOnly
            || ConnectionFor(document) is not { Client: var client, Sync: var sync }
            || !client.OnTypeFormattingTriggerCharacters.Contains(character))
            return;

        sync.Flush();
        var version = sync.Version;
        var location = document.Document.GetLocation(caret);
        try
        {
            var edits = await client.OnTypeFormattingAsync(
                document.Uri, location.Line - 1, location.Column - 1, character, tabSize: 4, insertSpaces: true);

            // Typed on in the meantime: the edits are for the older text.
            if (edits.Count == 0 || sync.HasPendingChanges || sync.Version != version)
                return;
            ApplyTextEdits(document, edits.Select(edit => (edit.Range, edit.NewText)));
        }
        catch (Exception)
        {
            // Not worth interrupting the typing for: the text stays as typed, and Format Document still works.
        }
    }

    /// <summary>XAML Styler formatting (no language server involved), applied as line edits in one undo step.</summary>
    private bool FormatXaml(OpenDocument document, bool reportUnchanged)
    {
        if (document.IsReadOnly)
        {
            if (reportUnchanged)
                Status("This document is read-only.");
            return false;
        }

        try
        {
            var text = document.Document.Text;
            var formatted = XamlFormatter.Format(text, document.Path, _workspaceRoot, _settings.Features.XamlIndentSize);
            if (formatted == text)
            {
                if (reportUnchanged)
                    Status("The document is already formatted.");
                return true;
            }

            using (document.Document.RunUpdate())   // one undo step
            {
                // Back to front, so earlier offsets stay valid.
                var edits = XamlFormatter.LineEdits(text, formatted);
                for (var index = edits.Count - 1; index >= 0; index--)
                    document.Document.Replace(edits[index].Start, edits[index].Length, edits[index].NewText);
            }

            if (reportUnchanged)
                Status("Document formatted. Review and save the changes.");
            return true;
        }
        catch (XmlException ex)
        {
            Status($"Can't format: the XAML isn't well-formed ({ex.Message}).");
            return false;
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

        using (document.Document.RunUpdate())   // one undo step for the whole format
        {
            foreach (var (start, end, newText) in offsets)
                document.Document.Replace(start, end - start, newText);
        }
    }

    private void Status(string text) => _messenger.Send(new StatusMessage(text));
}
