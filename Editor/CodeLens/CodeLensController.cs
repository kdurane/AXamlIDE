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
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.CodeLens;

/// <summary>One line of the references list: where it is, and that line of code.</summary>
public sealed record ReferenceRow(string Place, string Preview, LspLocation Location);

/// <summary>
/// Code lenses for C# (Visual Studio's / VS Code's "3 references" above declarations), from the C# language server.
/// Fetched when a document is shown or connected, a moment after typing stops, and when the server says they
/// changed (workspace/codeLens/refresh). Clicking one lists the references (EditorPaneView.axaml's
/// ReferencesPopup); clicking a reference, or Enter, goes there.
/// </summary>
public sealed class CodeLensController : IDisposable
{
    private static readonly TimeSpan FetchDelay = TimeSpan.FromMilliseconds(800);
    private const int MaxRetries = 5;

    private readonly EditorContext _ctx;
    private readonly NavigationService _navigation;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly TextBlock _title;
    private readonly CodeLensGenerator _generator;
    private readonly DispatcherTimer _fetchTimer;
    private CancellationTokenSource _fetchCts = new();
    private LspClient? _refreshSource;
    private int _retries;

    public CodeLensController(EditorContext ctx, NavigationService navigation, Popup popup, ListBox list, TextBlock title)
    {
        _ctx = ctx;
        _navigation = navigation;
        _popup = popup;
        _list = list;
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
        ctx.TextChanged += _ => ScheduleFetch();
        ctx.Languages.ClientAttached += OnClientAttached;

        popup.PlacementTarget = textView;
        list.AddHandler(InputElement.TappedEvent, (_, _) => GoToSelected());
        list.KeyDown += OnListKeyDown;
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
        IReadOnlyList<LspLocation> locations;
        try
        {
            locations = await client.ReferencesAsync(document.Uri, start.StartLine, start.StartChar, includeDeclaration: false);
        }
        catch (Exception ex) when (ex is LspException or IOException or InvalidOperationException)
        {
            _ctx.Status($"Couldn't list the references: {ex.Message}");
            return;
        }

        if (locations.Count == 0)
        {
            _ctx.Status("No references.");
            return;
        }

        var rows = locations
            .OrderBy(location => location.Uri, StringComparer.Ordinal)
            .ThenBy(location => location.Range.StartLine)
            .Select(location => new ReferenceRow(Place(location), Preview(location), location))
            .ToList();

        _title.Text = lens.Item.Title;
        _list.ItemsSource = rows;
        _list.SelectedIndex = 0;

        var textView = _ctx.Editor.TextArea.TextView;
        var at = lens.TranslatePoint(new Point(0, lens.TextBounds.Bottom), textView) ?? default;
        _popup.PlacementRect = new Rect(at.X, at.Y, 1, 1);
        _popup.IsOpen = true;
        _list.Focus();   // asked for, so the keys work in it (Enter, arrows, Escape)
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                GoToSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                _ctx.Editor.TextArea.Focus();
                e.Handled = true;
                break;
        }
    }

    private void GoToSelected()
    {
        if (_list.SelectedItem is not ReferenceRow row)
            return;
        _popup.IsOpen = false;
        _navigation.ToLocation(row.Location);
    }

    /// <summary>"RunViewModel.cs:42".</summary>
    private static string Place(LspLocation location)
        => $"{Path.GetFileName(LocalPath(location))}:{location.Range.StartLine + 1}";

    /// <summary>The referencing line of code: from this editor's document when it's in it (unsaved edits), else from disk.</summary>
    private string Preview(LspLocation location)
    {
        var path = LocalPath(location);
        var line = location.Range.StartLine;
        try
        {
            if (_ctx.Active is { } active && string.Equals(active.Path, path, StringComparison.Ordinal)
                                          && line < active.Document.LineCount)
                return active.Document.GetText(active.Document.GetLineByNumber(line + 1)).Trim();
            return File.Exists(path) ? File.ReadLines(path).Skip(line).FirstOrDefault()?.Trim() ?? "" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string LocalPath(LspLocation location)
        => Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : location.Uri;

    public void Dispose()
    {
        _ctx.Languages.ClientAttached -= OnClientAttached;
        _refreshSource?.CodeLensRefreshRequested -= OnRefreshRequested;
        _refreshSource = null;
        _fetchTimer.Stop();
    }
}
