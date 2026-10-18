using System;
using System.Collections.Generic;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// Debounced didChange sync for one document. Hooks Changing (not Changed) so the
/// document is still in its pre-edit state and GetLocation gives the range LSP wants.
/// Falls back to full-text sync if the server doesn't advertise incremental.
/// </summary>
public sealed class DocumentSync : IDisposable
{
    private readonly TextDocument _doc;
    private readonly ILspSender _sender;
    private readonly string _uri;
    private readonly DispatcherTimer _timer;
    private readonly List<ContentChange> _pending = [];
    private bool _disposed;

    /// <summary>Version of the last text sent to the server (didOpen was version 1).</summary>
    public int Version { get; private set; } = 1;
    public bool HasPendingChanges => _pending.Count > 0;

    /// <summary>Raised after a batch of edits has been sent to the server.</summary>
    public event Action? Flushed;

    public DocumentSync(TextDocument doc, ILspSender sender, string uri)
    {
        _doc = doc;
        _sender = sender;
        _uri = uri;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Flush();

        _doc.Changing += OnChanging;
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_disposed) return;

        // AvaloniaEdit locations are 1-based; LSP is 0-based.
        var start = _doc.GetLocation(e.Offset);
        var end = _doc.GetLocation(e.Offset + e.RemovalLength);

        _pending.Add(new ContentChange(
            start.Line - 1, start.Column - 1,
            end.Line - 1, end.Column - 1,
            e.InsertedText.Text));

        _timer.Stop();
        _timer.Start();   // restart debounce
    }

    public void Flush()
    {
        _timer.Stop();
        if (_disposed || _pending.Count == 0) return;

        Version++;

        if (_sender.SupportsIncrementalSync)
            _sender.DidChange(_uri, Version, [.. _pending]);
        else
            _sender.DidChangeFull(_uri, Version, _doc.Text);

        _pending.Clear();
        Flushed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _doc.Changing -= OnChanging;
        _timer.Stop();
    }
}
