using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Semantic colouring: names are coloured by what they are (class, interface, struct, parameter, local, field,
/// method...), as Visual Studio, Rider and VS Code do. TextMate colours by syntax alone, so it can't tell that Console
/// is a class or that count is a parameter; the language server can (textDocument/semanticTokens).
///
/// Only the lines on screen are asked about, a moment after typing stops (see <see cref="VisibleLinesFetcher"/>);
/// until the answer comes, edited text keeps TextMate's colours.
/// </summary>
public sealed class SemanticHighlightController : IDisposable
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);

    private readonly EditorContext _ctx;
    private readonly IRegistryOptions _registry;
    private readonly SemanticColorizer _colorizer = new();
    private readonly VisibleLinesFetcher _fetcher;
    private SemanticTokenColors? _colors;
    private LspClient? _refreshSource;

    /// <summary>Construct after TextMate is installed: its colours have to be drawn first, so these go on top.</summary>
    public SemanticHighlightController(EditorContext ctx, IRegistryOptions registry)
    {
        _ctx = ctx;
        _registry = registry;
        ctx.Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _fetcher = new VisibleLinesFetcher(ctx, TypingDelay,
            client => client.SupportsSemanticTokens && ctx.Settings.Features.SemanticHighlighting, FetchAsync);
        ctx.Detaching += _ => _colorizer.Clear();
        ctx.FeaturesChanged += () =>
        {
            if (ctx.Settings.Features.SemanticHighlighting)
            {
                _fetcher.Refresh();
            }
            else if (!_colorizer.IsEmpty)
            {
                _colorizer.Clear();
                ctx.Editor.TextArea.TextView.Redraw();
            }
        };
    }

    /// <summary>The editor's theme changed: take the colours from the new one.</summary>
    public void SetTheme(IRawTheme theme)
    {
        _colors = new SemanticTokenColors(theme, _registry);
        _fetcher.Refresh();
    }

    private async Task<Action?> FetchAsync(LspClient client, OpenDocument document, LspRange range, CancellationToken ct)
    {
        WatchRefresh(client);
        var tokens = await client.SemanticTokensAsync(document.Uri, range, ct);
        return () => Show(document, tokens);
    }

    private void Show(OpenDocument document, IReadOnlyList<LspSemanticToken> tokens)
    {
        if (_colors is not { } colors)
            return;

        var text = document.Document;
        var segments = new List<SemanticColorSegment>();
        foreach (var token in tokens)
        {
            if (colors.For(token.Type) is not { } brush || token.Line >= text.LineCount)
                continue;
            var line = text.GetLineByNumber(token.Line + 1);
            var start = line.Offset + Math.Min(token.Character, line.Length);
            var end = Math.Min(start + token.Length, line.EndOffset);
            if (end > start)
                segments.Add(new SemanticColorSegment(brush) { StartOffset = start, Length = end - start });
        }

        if (segments.Count == 0 && _colorizer.IsEmpty)
            return;
        _colorizer.Set(text, segments);
        _ctx.Editor.TextArea.TextView.Redraw();
    }

    private void WatchRefresh(LspClient client)
    {
        if (ReferenceEquals(_refreshSource, client))
            return;
        _refreshSource?.SemanticTokensRefreshRequested -= _fetcher.Refresh;
        _refreshSource = client;
        client.SemanticTokensRefreshRequested += _fetcher.Refresh;
    }

    public void Dispose()
    {
        _refreshSource?.SemanticTokensRefreshRequested -= _fetcher.Refresh;
        _refreshSource = null;
        _fetcher.Dispose();
    }
}
