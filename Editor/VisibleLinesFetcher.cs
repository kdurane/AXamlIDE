using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor;

/// <summary>Lines of the document, 0-based as LSP counts them, both included.</summary>
public readonly record struct LineSpan(int First, int Last)
{
    public bool Contains(LineSpan other) => other.First >= First && other.Last <= Last;

    /// <summary>The lines as an LSP range: from the start of the first to the end of the last.</summary>
    public LspRange ToRange(TextDocument document)
        => new(First, 0, Last, document.GetLineByNumber(Last + 1).Length);
}

/// <summary>
/// For editor features that ask the language server about the lines on screen (semantic colours, inlay hints), so
/// they never work on the whole file: decides when to ask again, and only applies an answer that's still current.
///
/// It asks for the visible lines plus a screenful either side (so scrolling a little needs nothing new):
///   - when a document is shown, or connects to its server;
///   - a moment after typing stops (<c>typingDelay</c>), not on every key;
///   - when scrolling shows lines outside the ones last asked about;
///   - when the server says its answers changed (<see cref="Refresh"/>), or has finished loading its projects (until
///     then it answers with nothing).
/// An answer is thrown away if the text changed or another request started while it was on its way.
/// </summary>
public sealed class VisibleLinesFetcher : IDisposable
{
    private static readonly TimeSpan ScrollDelay = TimeSpan.FromMilliseconds(100);

    private readonly EditorContext _ctx;
    private readonly Func<LspClient, bool> _supported;
    private readonly Func<LspClient, OpenDocument, LspRange, CancellationToken, Task<Action?>> _fetch;
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _typingDelay;
    private CancellationTokenSource _cts = new();
    private LineSpan? _covered;   // what the current answer covers; null when there's none (or it's out of date)
    private bool _fetching;
    private LspClient? _client;   // whose project loading is watched

    /// <param name="supported">Whether a server can answer (its capability).</param>
    /// <param name="fetch">
    /// Asks the server about the lines and returns how to show the answer (or null for nothing to show). It runs only
    /// if the answer is still current when it arrives.
    /// </param>
    public VisibleLinesFetcher(
        EditorContext ctx,
        TimeSpan typingDelay,
        Func<LspClient, bool> supported,
        Func<LspClient, OpenDocument, LspRange, CancellationToken, Task<Action?>> fetch)
    {
        _ctx = ctx;
        _typingDelay = typingDelay;
        _supported = supported;
        _fetch = fetch;

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = FetchAsync();
        };

        ctx.Attached += _ => Refresh();
        ctx.Detaching += _ => Cancel();
        ctx.TextChanged += _ =>
        {
            Cancel();
            Schedule(_typingDelay);
        };
        ctx.Languages.ClientAttached += OnClientAttached;

        // Scrolled, resized, or shown again (a tab brought back): ask about the lines that are new on screen.
        ctx.Editor.TextArea.TextView.VisualLinesChanged += (_, _) =>
        {
            if (!_fetching && !_timer.IsEnabled && VisibleLines() is { } visible
                && (_covered is not { } covered || !covered.Contains(visible)))
                Schedule(ScrollDelay);
        };
    }

    private void OnClientAttached(OpenDocument document)
    {
        if (ReferenceEquals(document, _ctx.Active))
            Refresh();
    }

    /// <summary>Ask again soon: the server's answers changed. Safe from any thread.</summary>
    public void Refresh() => Dispatcher.UIThread.Post(() =>
    {
        _covered = null;
        Schedule(ScrollDelay);
    });

    private void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private void Cancel()
    {
        _timer.Stop();
        _cts.Cancel();
        _covered = null;
        _fetching = false;
    }

    private async Task FetchAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _fetching = false;
        var ct = _cts.Token;

        if (_ctx.Active is not { } document
            || _ctx.Languages.ConnectionFor(document) is not { Client: var client, Sync: var sync }
            || !_supported(client)
            || VisibleLines() is not { } visible)
            return;

        // A screenful above and below too.
        var lineCount = document.Document.LineCount;
        var margin = visible.Last - visible.First + 1;
        var wanted = new LineSpan(Math.Max(0, visible.First - margin), Math.Min(lineCount - 1, visible.Last + margin));

        WatchProjectLoading(client);
        sync.Flush();   // the server has to see what's on screen
        var version = document.Document.Version;
        Action? show;
        _fetching = true;
        try
        {
            show = await _fetch(client, document, wanted.ToRange(document.Document), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is LspException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"[visible lines] {ex.Message}");
            return;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                _fetching = false;   // a cancelled one was replaced by a newer request, still going
        }

        if (ct.IsCancellationRequested || !ReferenceEquals(document, _ctx.Active)
            || document.Document.Version is not { } now || !now.BelongsToSameDocumentAs(version) || now.CompareAge(version) != 0)
            return;   // out of date: the next request is on its way

        show?.Invoke();
        _covered = wanted;
    }

    private void WatchProjectLoading(LspClient client)
    {
        if (ReferenceEquals(_client, client))
            return;
        _client?.ProjectInitializationComplete -= Refresh;
        _client = client;
        client.ProjectInitializationComplete += Refresh;
    }

    /// <summary>The lines on screen, or null when the editor isn't (a background tab).</summary>
    private LineSpan? VisibleLines()
    {
        var textView = _ctx.Editor.TextArea.TextView;
        if (TopLevel.GetTopLevel(textView) is null || textView.Bounds.Height <= 0 || textView.Document is not { } text)
            return null;
        var top = textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y);
        var bottom = textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y + textView.Bounds.Height);
        return top is null || bottom is null
            ? null
            : new LineSpan(top.LineNumber - 1, Math.Min(text.LineCount, bottom.LineNumber) - 1);
    }

    public void Dispose()
    {
        Cancel();
        _ctx.Languages.ClientAttached -= OnClientAttached;
        _client?.ProjectInitializationComplete -= Refresh;
        _client = null;
    }
}
