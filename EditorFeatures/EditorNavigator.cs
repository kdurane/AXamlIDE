using System;
using System.IO;
using Avalonia.Threading;
using AXamlIDE.Lsp;
using AXamlIDE.Models;

namespace AXamlIDE.EditorFeatures;

/// <summary>
/// Every "go to this place in the editor" move. These were scattered through MainWindow as
/// NavigateToProblem, NavigateToDebugLocation, OnSearchResultClick, OnOutlineSelectionChanged
/// and NavigateToLocationAsync. Bodies are copy-paste; _active -> _ctx.Active,
/// DocTabs.SelectedItem = ... -> _ctx.ActivateDocument(...).
/// </summary>
public sealed class EditorNavigator(EditorContext ctx)
{
    private readonly EditorContext _ctx = ctx;

    /// <summary>Was NavigateToProblem (and the OnProblemClick target).</summary>
    public void ToProblem(ProblemItem problem)
    {
        var editor = _ctx.Editor;

        _ctx.ActivateDocument(problem.Document);
        editor.CaretOffset = Math.Min(problem.Offset, editor.Document.TextLength);
        editor.Focus();

        Dispatcher.UIThread.Post(() =>
        {
            var caret = editor.TextArea.Caret;
            editor.ScrollTo(caret.Line, caret.Column);
        }, DispatcherPriority.Render);
    }

    /// <summary>Was NavigateToDebugLocation.</summary>
    public void ToDebugLocation(string path, int line)
    {
        var editor = _ctx.Editor;

        OpenDocument document;
        try
        {
            document = _ctx.ViewModel.OpenDocument(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Status($"Couldn't open the stopped source location: {ex.Message}");
            return;
        }

        _ctx.ActivateDocument(document);

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_ctx.Active, document))
                return;

            var lineNumber = Math.Clamp(line, 1, document.Document.LineCount);
            editor.CaretOffset = document.Document.GetLineByNumber(lineNumber).Offset;
            editor.ScrollToLine(lineNumber);
            editor.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Was the body of OnSearchResultClick (MainWindow keeps the Click handler).</summary>
    public void ToSearchResult(WorkspaceSearchResult result)
    {
        var editor = _ctx.Editor;

        try
        {
            _ctx.OpenAndActivate(result.FilePath);
            if (editor.Document.LineCount < result.LineNumber)
                return;

            var line = editor.Document.GetLineByNumber(result.LineNumber);
            var column = Math.Clamp(result.Column, 0, line.Length);
            var offset = line.Offset + column;
            var searchQuery = _ctx.ViewModel.Search.SearchQuery;
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

    /// <summary>Was the body of OnOutlineSelectionChanged (MainWindow keeps the SelectionChanged handler).</summary>
    public void ToSelectedOutlineItem()
    {
        var editor = _ctx.Editor;

        if (_ctx.ViewModel.SelectedDocumentOutlineItem is not { } item
            || item.StartOffset <= editor.CaretOffset && editor.CaretOffset <= item.EndOffset)
            return;

        editor.CaretOffset = Math.Clamp(item.NavigationOffset, 0, editor.Document.TextLength);
        editor.Focus();

        Dispatcher.UIThread.Post(() =>
        {
            var caret = editor.TextArea.Caret;
            editor.ScrollTo(caret.Line, caret.Column);
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Was NavigateToLocationAsync. It never actually awaited anything (just
    /// <c>await Task.CompletedTask</c>), so it is synchronous now.
    /// </summary>
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
            var document = _ctx.OpenAndActivate(uri.LocalPath);
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
}
