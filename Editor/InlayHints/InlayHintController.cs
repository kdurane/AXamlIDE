using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.InlayHints;

/// <summary>
/// Inlay hints for C#, as Rider and Visual Studio show them: parameter names before literal arguments
/// (Process(force: true)) and the types behind var, lambda parameters and new(). Which ones the server sends is set by
/// its options (<see cref="RoslynSettings"/>).
///
/// Only the lines on screen are asked about, a moment after typing stops (see <see cref="VisibleLinesFetcher"/>).
/// </summary>
public sealed class InlayHintController : IDisposable
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(500);

    private readonly EditorContext _ctx;
    private readonly InlayHintGenerator _generator;
    private readonly VisibleLinesFetcher _fetcher;
    private LspClient? _refreshSource;

    public InlayHintController(EditorContext ctx)
    {
        _ctx = ctx;

        var textView = ctx.Editor.TextArea.TextView;
        _generator = new InlayHintGenerator(hints => new InlayHintControl(hints, textView, ctx.Editor.FontFamily, ctx.Editor.FontSize));
        textView.ElementGenerators.Add(_generator);

        _fetcher = new VisibleLinesFetcher(ctx, TypingDelay, client => client.SupportsInlayHints && AnyWanted, FetchAsync);
        ctx.Detaching += _ => _generator.Clear();

        // Which kinds are shown is the server's option (RoslynSettings): it's told, asks for its options again, and
        // ConfigurationAnswered (WatchRefresh) brings new hints. Both kinds off: nothing to ask for.
        ctx.FeaturesChanged += () =>
        {
            if (AnyWanted || _generator.IsEmpty)
                return;
            _generator.Clear();
            ctx.Editor.TextArea.TextView.Redraw();
        };
    }

    private bool AnyWanted => _ctx.Settings.Features is { InlayParameterHints: true } or { InlayTypeHints: true };

    private async Task<Action?> FetchAsync(LspClient client, OpenDocument document, LspRange range, CancellationToken ct)
    {
        WatchRefresh(client);
        var hints = await client.InlayHintsAsync(document.Uri, range, ct);
        return () => Show(document, hints);
    }

    private void Show(OpenDocument document, IReadOnlyList<LspInlayHint> hints)
    {
        if (hints.Count == 0 && _generator.IsEmpty)
            return;

        var text = document.Document;
        var segments = new List<InlayHintSegment>(hints.Count);
        foreach (var hint in hints)
            if (hint.Line < text.LineCount)
                segments.Add(new InlayHintSegment(hint) { StartOffset = document.OffsetOf(hint.Line, hint.Character), Length = 0 });

        _generator.Set(text, segments);
        _ctx.Editor.TextArea.TextView.Redraw();
    }

    private void WatchRefresh(LspClient client)
    {
        if (ReferenceEquals(_refreshSource, client))
            return;
        _refreshSource?.InlayHintsRefreshRequested -= _fetcher.Refresh;
        _refreshSource?.ConfigurationAnswered -= _fetcher.Refresh;
        _refreshSource = client;
        client.InlayHintsRefreshRequested += _fetcher.Refresh;
        client.ConfigurationAnswered += _fetcher.Refresh;
    }

    public void Dispose()
    {
        _refreshSource?.InlayHintsRefreshRequested -= _fetcher.Refresh;
        _refreshSource?.ConfigurationAnswered -= _fetcher.Refresh;
        _refreshSource = null;
        _fetcher.Dispose();
    }
}
