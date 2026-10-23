using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.CodeLens;

/// <summary>
/// Code lenses for C# (Visual Studio's / VS Code's "3 references" above declarations), from the C# language server.
/// Fetched when a document is shown or connected, a moment after typing stops, and when the server says they
/// changed (workspace/codeLens/refresh). Clicking one lists the references (EditorPaneView.axaml's
/// ReferencesPopup), grouped by file as VS Code's peek and Rider's usages list do; clicking a reference, or Enter,
/// goes there.
/// </summary>
public sealed class CodeLensController : IDisposable
{
    private static readonly TimeSpan FetchDelay = TimeSpan.FromMilliseconds(800);
    private const int MaxRetries = 5;

    private readonly EditorContext _ctx;
    private readonly NavigationService _navigation;
    private readonly Popup _popup;
    private readonly TreeView _tree;
    private readonly TextBlock _title;
    private readonly CodeLensGenerator _generator;
    private readonly DispatcherTimer _fetchTimer;
    private CancellationTokenSource _fetchCts = new();
    private LspClient? _refreshSource;
    private int _retries;

    public CodeLensController(EditorContext ctx, NavigationService navigation, Popup popup, TreeView tree, TextBlock title)
    {
        _ctx = ctx;
        _navigation = navigation;
        _popup = popup;
        _tree = tree;
        _title = title;

        var textView = ctx.Editor.TextArea.TextView;
        _generator = new CodeLensGenerator(item =>
            new CodeLensControl(item, textView, ctx.Editor.FontFamily, ctx.Editor.FontSize, ShowReferences));
        textView.ElementGenerators.Add(_generator);

        _fetchTimer = new DispatcherTimer { Interval = FetchDelay };
        _fetchTimer.Tick += (_, _) =>
        {
            _fetchTimer.Stop();
            _ = FetchAsync();
        };

        ctx.Attached += _ => OnDocumentShown();
        ctx.FeaturesChanged += () => _ = FetchAsync();
        ctx.TextChanged += _ => ScheduleFetch();
        ctx.Languages.ClientAttached += OnClientAttached;

        popup.PlacementTarget = textView;
        tree.AddHandler(InputElement.TappedEvent, OnTreeTapped);
        // On the way down: a TreeViewItem uses Enter itself (to open and close), before it would bubble up to here.
        tree.AddHandler(InputElement.KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnClientAttached(OpenDocument document)
    {
        if (ReferenceEquals(document, _ctx.Active))
            _ = FetchAsync();
    }

    private void OnDocumentShown()
    {
        _retries = 0;
        SetItems([]);
        _ = FetchAsync();
    }

    private void ScheduleFetch()
    {
        _fetchTimer.Stop();
        _fetchTimer.Start();
    }

    // ---------------------------------------------------------------- fetching

    private async Task FetchAsync()
    {
        _fetchCts.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        if (_ctx.Active is not { LanguageId: LanguageIds.CSharp } document
            || !_ctx.Settings.Features.CodeLens
            || _ctx.Languages.ConnectionFor(document) is not { Client: { SupportsCodeLens: true } client, Sync: var sync })
        {
            SetItems([]);
            return;
        }

        WatchRefresh(client);
        sync.Flush();   // the server has to see what's on screen
        try
        {
            var lenses = await client.CodeLensAsync(document.Uri, ct);
            var resolved = await Task.WhenAll(lenses.Select(lens => client.ResolveCodeLensAsync(lens, ct)));
            if (ct.IsCancellationRequested || !ReferenceEquals(document, _ctx.Active))
                return;

            var text = document.Document;
            SetItems([.. resolved
                .Where(lens => lens.Title is { Length: > 0 } && lens.Range.StartLine < text.LineCount)
                .Select(lens => new CodeLensItem(
                    text.CreateAnchor(Math.Min(document.OffsetOf(lens.Range.StartLine, lens.Range.StartChar), text.TextLength)),
                    lens.Title!, lens))]);
            _retries = 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (LspException) when (_retries < MaxRetries)
        {
            // Usually "resolve version does not match": the server moved on (still loading, or an edit). Ask again.
            _retries++;
            ScheduleFetch();
        }
        catch (Exception ex) when (ex is LspException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"[code lens] {ex.Message}");
        }
    }

    private void WatchRefresh(LspClient client)
    {
        if (ReferenceEquals(_refreshSource, client))
            return;
        _refreshSource?.CodeLensRefreshRequested -= OnRefreshRequested;
        _refreshSource = client;
        client.CodeLensRefreshRequested += OnRefreshRequested;
    }

    private void OnRefreshRequested() => Dispatcher.UIThread.Post(ScheduleFetch);

    private void SetItems(IReadOnlyList<CodeLensItem> items)
    {
        if (items.Count == 0 && _generator.Items.Count == 0)
            return;
        _generator.Items = items;
        _ctx.Editor.TextArea.TextView.Redraw();
    }

    // ---------------------------------------------------------------- the references list

    private async void ShowReferences(CodeLensControl lens)
    {
        if (_ctx.Active is not { } document || _ctx.Languages.ConnectionFor(document) is not { Client: var client, Sync: var sync })
            return;

        sync.Flush();
        var start = lens.Item.Lens.Range;
        IReadOnlyList<ReferenceGroup> groups;
        try
        {
            var locations = await client.ReferencesAsync(document.Uri, start.StartLine, start.StartChar, includeDeclaration: false);
            if (locations.Count == 0)
            {
                _ctx.Status("No references.");
                return;
            }
            groups = await GroupAsync(client, document, locations);
        }
        catch (Exception ex) when (ex is LspException or IOException or InvalidOperationException)
        {
            _ctx.Status($"Couldn't list the references: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(document, _ctx.Active))
            return;

        // Counted from the list, not the lens: the lens may be from before the server had seen everything.
        var count = groups.Sum(group => group.Count);
        _title.Text = count == 1 ? "1 reference" : $"{count} references";
        _tree.ItemsSource = groups;
        var selected = (object?)groups.FirstOrDefault(group => group.IsExpanded)?.Items[0] ?? groups[0];
        _tree.SelectedItem = selected;

        var textView = _ctx.Editor.TextArea.TextView;
        var at = lens.TranslatePoint(new Point(0, lens.TextBounds.Bottom), textView) ?? default;
        _popup.PlacementRect = new Rect(at.X, at.Y, 1, 1);
        _popup.IsOpen = true;

        // Asked for, so the keys work in it (Enter, arrows, Escape). Once the popup has laid out its rows: before that
        // there's nothing to focus.
        Dispatcher.UIThread.Post(() => (_tree.TreeContainerFromItem(selected) as Control ?? _tree).Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The references by file: this file first, then the others by name, source-generated ones last; each file's in
    /// the order they come in it.
    /// </summary>
    private async Task<IReadOnlyList<ReferenceGroup>> GroupAsync(LspClient client, OpenDocument current, IReadOnlyList<LspLocation> locations)
    {
        var groups = new List<ReferenceGroup>();
        foreach (var file in locations.GroupBy(location => location.Uri, StringComparer.Ordinal))
        {
            var uri = file.Key;
            var generated = LspHelpers.IsSourceGenerated(uri);
            var lines = await LinesOfAsync(client, current, uri);
            var items = file
                .OrderBy(location => location.Range.StartLine).ThenBy(location => location.Range.StartChar)
                .Select(location => new ReferenceItem(location, Preview(lines, location.Range)))
                .ToList();
            var path = generated ? null : LocalPath(uri);
            groups.Add(new ReferenceGroup(
                generated ? LspHelpers.SourceGeneratedName(uri) : Path.GetFileName(path!),
                generated ? "source generated" : Path.GetFileName(Path.GetDirectoryName(path)),
                generated,
                items));
        }

        var currentName = Path.GetFileName(current.Path);
        return [.. groups
            .OrderBy(group => group.IsSourceGenerated)
            .ThenBy(group => group.Name != currentName)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A file's lines: from this editor's document when it's that file (unsaved edits count), from the language server
    /// for a source generator's output (it isn't on disk), else from disk. Empty when they can't be read.
    /// </summary>
    private static async Task<string[]> LinesOfAsync(LspClient client, OpenDocument current, string uri)
    {
        try
        {
            if (LspHelpers.IsSourceGenerated(uri))
                return SplitLines(await client.SourceGeneratedTextAsync(uri) ?? "");
            var path = LocalPath(uri);
            if (string.Equals(current.Path, path, StringComparison.Ordinal))
                return SplitLines(current.Document.Text);
            return File.Exists(path) ? await File.ReadAllLinesAsync(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LspException)
        {
            return [];
        }
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static ReferencePreview Preview(string[] lines, LspRange range)
    {
        if (range.StartLine >= lines.Length)
            return new ReferencePreview("", "", "");
        var end = range.EndLine == range.StartLine ? range.EndChar : int.MaxValue;
        return ReferencePreview.Of(lines[range.StartLine], range.StartChar, end);
    }

    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;   // a group's arrow: that opens and closes the group by itself
        OpenSelected();
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                _ctx.Editor.TextArea.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>A reference: go there. A file: open or close its group.</summary>
    private void OpenSelected()
    {
        switch (_tree.SelectedItem)
        {
            case ReferenceItem item:
                _popup.IsOpen = false;
                _navigation.ToLocation(item.Location);
                break;
            case ReferenceGroup group:
                group.IsExpanded = !group.IsExpanded;
                break;
        }
    }

    private static string LocalPath(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;

    public void Dispose()
    {
        _ctx.Languages.ClientAttached -= OnClientAttached;
        _refreshSource?.CodeLensRefreshRequested -= OnRefreshRequested;
        _refreshSource = null;
        _fetchTimer.Stop();
    }
}
