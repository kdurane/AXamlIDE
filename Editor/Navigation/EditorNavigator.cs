using System;
using System.IO;
using Avalonia.Threading;
using AXamlIDE.Editor.Debugging;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Editor.Navigation;

/// <summary>
/// Every "go to this place in the editor" move. Driven by messages (see EditorMessages.cs);
/// it never touches a view model, and opens files through IDocumentHost.
/// NavigationCommands also calls the public methods directly.
/// </summary>
public sealed class EditorNavigator :
    IRecipient<NavigateToProblemMessage>,
    IRecipient<NavigateToDebugLocationMessage>,
    IRecipient<NavigateToSearchResultMessage>,
    IRecipient<NavigateToOutlineItemMessage>,
    IRecipient<NavigateToLocationMessage>,
    IRecipient<DebugSnapshotChangedMessage>
{
    private readonly EditorContext _ctx;
    private readonly DebugInlineValueGenerator _inlineValues = new();
    private bool _inlineValuesAttached;
    private StopSnapshot? _debugSnapshot;

    public EditorNavigator(EditorContext ctx)
    {
        _ctx = ctx;
        _ctx.Messenger.RegisterAll(this);

        // Switching tabs while paused: show (or clear) the inline values for the new document.
        _ctx.Attached += _ => ApplyDebugValues();
    }

    public void Dispose() => _ctx.Messenger.UnregisterAll(this);

    // ------------------------------------------------------------- message entry points

    public void Receive(NavigateToProblemMessage m) => ToProblem(m.Problem);
    public void Receive(NavigateToDebugLocationMessage m) => ToDebugLocation(m.Path, m.Line);
    public void Receive(NavigateToSearchResultMessage m) => ToSearchResult(m.Result, m.SearchQuery);
    public void Receive(NavigateToOutlineItemMessage m) => ToOutlineItem(m.Item);
    public void Receive(NavigateToLocationMessage m) => ToLocation(m.Location);
    public void Receive(DebugSnapshotChangedMessage m) => ShowDebugValues(m.Snapshot);

    // ------------------------------------------------------------------ debug values

    /// <summary>A null snapshot means the debuggee is running again or has ended.</summary>
    public void ShowDebugValues(StopSnapshot? snapshot)
    {
        _debugSnapshot = snapshot;
        ApplyDebugValues();
    }

    private void ApplyDebugValues()
    {
        var editor = _ctx.Editor;
        if (!_inlineValuesAttached)
        {
            editor.TextArea.TextView.ElementGenerators.Add(_inlineValues);
            _inlineValuesAttached = true;
        }

        if (_debugSnapshot?.Top is { Path: { } path } top
            && _ctx.Active is { } active
            && string.Equals(path, active.Path, StringComparison.Ordinal))
            _inlineValues.Show(_debugSnapshot.Values, top.Line);
        else
            _inlineValues.Clear();

        editor.TextArea.TextView.Redraw();
    }

    // ------------------------------------------------------------------- navigation

    public void ToProblem(ProblemItem problem)
    {
        var editor = _ctx.Editor;

        _ctx.Documents.Activate(problem.Document);
        editor.CaretOffset = Math.Min(problem.Offset, editor.Document.TextLength);
        editor.Focus();

        ScrollCaretIntoView();
    }

    public void ToDebugLocation(string path, int line)
    {
        var editor = _ctx.Editor;

        OpenDocument document;
        try
        {
            document = _ctx.Documents.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Status($"Couldn't open the stopped source location: {ex.Message}");
            return;
        }

        _ctx.Documents.Activate(document);

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_ctx.Active, document))
                return;

            var lineNumber = Math.Clamp(line, 1, document.Document.LineCount);
            editor.CaretOffset = document.Document.GetLineByNumber(lineNumber).Offset;
            editor.ScrollToLine(lineNumber);
            editor.Focus();
            ApplyDebugValues();
        }, DispatcherPriority.Loaded);
    }

    /// <param name="searchQuery">The query that produced the result, used to select the match.</param>
    public void ToSearchResult(WorkspaceSearchResult result, string? searchQuery)
    {
        var editor = _ctx.Editor;

        try
        {
            _ctx.Documents.OpenAndActivate(result.FilePath);
            if (editor.Document.LineCount < result.LineNumber)
                return;

            var line = editor.Document.GetLineByNumber(result.LineNumber);
            var column = Math.Clamp(result.Column, 0, line.Length);
            var offset = line.Offset + column;
            var matchLength = 0;
            if (!string.IsNullOrEmpty(searchQuery)
                && column <= line.Length - searchQuery.Length
                && string.Compare(
                    editor.Document.Text,
                    offset,
                    searchQuery,
                    0,
                    searchQuery.Length,
                    StringComparison.OrdinalIgnoreCase) == 0)
                matchLength = searchQuery.Length;

            editor.Select(offset, matchLength);
            editor.TextArea.Caret.BringCaretToView();
            editor.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ctx.Status($"Couldn't open search result '{result.FilePath}': {ex.Message}");
        }
    }

    /// <param name="item">The outline entry to jump to (in the active document).</param>
    public void ToOutlineItem(DocumentOutlineItem item)
    {
        var editor = _ctx.Editor;

        if (item.StartOffset <= editor.CaretOffset && editor.CaretOffset <= item.EndOffset)
            return;

        editor.CaretOffset = Math.Clamp(item.NavigationOffset, 0, editor.Document.TextLength);
        editor.Focus();

        ScrollCaretIntoView();
    }

    public void ToLocation(LspLocation location)
    {
        if (!Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            _ctx.Status($"Cannot navigate to unsupported location '{location.Uri}'.");
            return;
        }

        try
        {
            var editor = _ctx.Editor;
            var document = _ctx.Documents.OpenAndActivate(uri.LocalPath);
            var offset = document.OffsetOf(location.Range.StartLine, location.Range.StartChar);
            editor.CaretOffset = offset;
            var endOffset = document.OffsetOf(location.Range.EndLine, location.Range.EndChar);
            editor.Select(offset, Math.Max(0, endOffset - offset));
            editor.TextArea.Caret.BringCaretToView();
            editor.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ctx.Status($"Couldn't open location '{uri.LocalPath}': {ex.Message}");
        }
    }

    private void ScrollCaretIntoView()
    {
        var editor = _ctx.Editor;
        Dispatcher.UIThread.Post(() =>
        {
            var caret = editor.TextArea.Caret;
            editor.ScrollTo(caret.Line, caret.Column);
        }, DispatcherPriority.Render);
    }
}
