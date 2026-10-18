using System;
using System.Threading;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// One open document's link to its language server: the client it was opened on, the didChange
/// sync, and the in-flight pull-diagnostics request. Owned by <see cref="LanguageSessionService"/>;
/// a document without one has no server (yet, or for its language).
/// </summary>
public sealed class DocumentConnection(LspClient client, DocumentSync sync) : IDisposable
{
    public LspClient Client { get; } = client;
    public DocumentSync Sync { get; } = sync;

    /// <summary>The in-flight pull-diagnostics request, if any (cancelled when a newer one starts).</summary>
    public CancellationTokenSource? PullCts { get; set; }

    public void Dispose()
    {
        PullCts?.Cancel();
        Sync.Dispose();
    }
}
