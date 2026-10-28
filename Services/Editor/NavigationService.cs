using System;
using System.IO;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// Every "go to this place" move (the NavigateTo* messages in EditorMessages.cs, plus Go to
/// Definition and friends, which call <see cref="ToLocation"/>).
///
/// It works on documents, not editors: it opens and activates the document, then asks it to
/// <see cref="OpenDocument.Reveal"/> the range. Whichever editor shows that document (in any window,
/// even one not created yet) does the selecting, scrolling and focusing.
/// </summary>
public sealed class NavigationService :
    IRecipient<NavigateToProblemMessage>,
    IRecipient<NavigateToDebugLocationMessage>,
    IRecipient<NavigateToSearchResultMessage>,
    IRecipient<NavigateToOutlineItemMessage>
{
    private readonly IDocumentHost _documents;
    private readonly IMessenger _messenger;

    public NavigationService(IDocumentHost documents, IMessenger messenger)
    {
        _documents = documents;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    public void Receive(NavigateToProblemMessage m) => ToProblem(m.Problem);
    public void Receive(NavigateToDebugLocationMessage m) => ToDebugLocation(m.Path, m.Line);
    public void Receive(NavigateToSearchResultMessage m) => ToSearchResult(m.Result, m.SearchQuery);
    public void Receive(NavigateToOutlineItemMessage m) => ToOutlineItem(m.Item);

    public void ToProblem(ProblemItem problem)
    {
        _documents.Activate(problem.Document);
        problem.Document.Reveal(problem.Offset);
    }

    public void ToDebugLocation(string path, int line)
    {
        try
        {
            var document = _documents.OpenAndActivate(path);
            var lineNumber = Math.Clamp(line, 1, document.Document.LineCount);
            document.Reveal(document.Document.GetLineByNumber(lineNumber).Offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't open the stopped source location: {ex.Message}");
        }
    }

    /// <param name="searchQuery">The query that produced the result, used to select the match.</param>
    public void ToSearchResult(WorkspaceSearchResult result, string? searchQuery)
    {
        try
        {
            var document = _documents.OpenAndActivate(result.FilePath);
            var text = document.Document;
            if (text.LineCount < result.LineNumber)
                return;

            var line = text.GetLineByNumber(result.LineNumber);
            var column = Math.Clamp(result.Column, 0, line.Length);
            var offset = line.Offset + column;
            var matchLength = 0;
            if (!string.IsNullOrEmpty(searchQuery)
                && column <= line.Length - searchQuery.Length
                && string.Compare(text.Text, offset, searchQuery, 0, searchQuery.Length,
                    StringComparison.OrdinalIgnoreCase) == 0)
                matchLength = searchQuery.Length;

            document.Reveal(offset, matchLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open search result '{result.FilePath}': {ex.Message}");
        }
    }

    /// <param name="item">The outline entry to jump to (in the selected tab).</param>
    public void ToOutlineItem(DocumentOutlineItem item)
    {
        if (_documents.Active is not { } document)
            return;
        if (item.StartOffset <= document.CaretOffset && document.CaretOffset <= item.EndOffset)
            return;

        document.Reveal(item.NavigationOffset);
    }

    public void ToLocation(LspLocation location)
    {
        if (!Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            Status($"Cannot navigate to unsupported location '{location.Uri}'.");
            return;
        }

        try
        {
            var document = _documents.OpenAndActivate(uri.LocalPath);
            var offset = document.OffsetOf(location.Range.StartLine, location.Range.StartChar);
            var endOffset = document.OffsetOf(location.Range.EndLine, location.Range.EndChar);
            document.Reveal(offset, Math.Max(0, endOffset - offset));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open location '{uri.LocalPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// A location in a source generator's output (not on disk), with its <paramref name="text"/> from Roslyn: opened
    /// read-only as {temp}/AXamlIDE/Generated/{hint name}, as Visual Studio shows generated files.
    /// </summary>
    public void ToGeneratedLocation(LspLocation location, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "AXamlIDE", "Generated", LspHelpers.SourceGeneratedName(location.Uri));
        var document = _documents.OpenReadOnly(path, text, LanguageIds.CSharp);
        if (document.Document.Text != text)
            document.Document.Text = text;   // open from an earlier visit: the generator has run since
        _documents.Activate(document);
        var offset = document.OffsetOf(location.Range.StartLine, location.Range.StartChar);
        var endOffset = document.OffsetOf(location.Range.EndLine, location.Range.EndChar);
        document.Reveal(offset, Math.Max(0, endOffset - offset));
        Status($"{LspHelpers.SourceGeneratedName(location.Uri)} is source-generated (read-only).");
    }

    private void Status(string text) => _messenger.Send(new StatusMessage(text));
}
