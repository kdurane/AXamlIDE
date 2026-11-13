using System;
using System.IO;
using System.Linq;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Navigation;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// Every "go to this place" move (the NavigateTo* messages in EditorMessages.cs, plus Go to
/// Definition and friends, which call <see cref="ToLocation"/>).
///
/// It works on documents, not editors: it opens and activates the document, then asks it to
/// <see cref="OpenDocument.Reveal"/> the range. Whichever editor shows that document (in any window,
/// even one not created yet) does the selecting, scrolling and focusing.
///
/// Every move is recorded in the <see cref="NavigationHistory"/> (where it came from and where it went), as is
/// switching tabs, so <see cref="Back"/> and <see cref="Forward"/> work like Visual Studio's Navigate Backward
/// and Forward.
/// </summary>
public sealed class NavigationService :
    IRecipient<NavigateToProblemMessage>,
    IRecipient<NavigateToDebugLocationMessage>,
    IRecipient<NavigateToSearchResultMessage>,
    IRecipient<NavigateToOutlineItemMessage>
{
    private readonly IDocumentHost _documents;
    private readonly DocumentManager _manager;
    private readonly NavigationHistory _history;
    private readonly IMessenger _messenger;
    private OpenDocument? _lastActive;

    public NavigationService(IDocumentHost documents, DocumentManager manager, NavigationHistory history, IMessenger messenger)
    {
        _documents = documents;
        _manager = manager;
        _history = history;
        _messenger = messenger;
        messenger.RegisterAll(this);

        // Going to another tab (clicked, or opened from the explorer) is a place to come back to.
        _lastActive = manager.Active;
        manager.ActiveChanged += OnActiveChanged;
        manager.Closing += document => history.Forget(document.Path);
    }

    public NavigationHistory History => _history;

    public void Receive(NavigateToProblemMessage m) => ToProblem(m.Problem);
    public void Receive(NavigateToDebugLocationMessage m) => ToDebugLocation(m.Path, m.Line);
    public void Receive(NavigateToSearchResultMessage m) => ToSearchResult(m.Result, m.SearchQuery);
    public void Receive(NavigateToOutlineItemMessage m) => ToOutlineItem(m.Item);

    // ---------------------------------------------------------------- back / forward

    /// <summary>Navigate Backward (Ctrl+-): to where the user was before the last jump or tab switch.</summary>
    public void Back() => GoTo(_history.Back());

    /// <summary>Navigate Forward (Ctrl+Shift+-): undo a Back.</summary>
    public void Forward() => GoTo(_history.Forward());

    private void GoTo(NavigationPoint? point)
    {
        if (point is null)
            return;
        if (!File.Exists(point.Path) && _manager.Documents.All(d => d.Path != point.Path))
        {
            _history.Forget(point.Path);   // gone since: skip it
            return;
        }

        _history.Navigating(() =>
        {
            try
            {
                var document = _documents.OpenAndActivate(point.Path);
                document.Reveal(Math.Min(point.Offset, document.Document.TextLength));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Status($"Couldn't go back to '{point.Path}': {ex.Message}");
            }
        });
    }

    private void OnActiveChanged(OpenDocument? document)
    {
        if (!_history.IsNavigating)
        {
            if (_lastActive is { } previous && !ReferenceEquals(previous, document))
                _history.Record(new NavigationPoint(previous.Path, previous.CaretOffset));
            if (document is not null)
                _history.Record(new NavigationPoint(document.Path, document.CaretOffset));
        }
        _lastActive = document;
    }

    /// <summary>
    /// A jump: record where the user is, make the move (a tab switch inside it isn't recorded again), then record
    /// where it went.
    /// </summary>
    private void Jump(Action<Action<OpenDocument, int>> move)
    {
        if (_manager.Active is { } from)
            _history.Record(new NavigationPoint(from.Path, from.CaretOffset));

        OpenDocument? target = null;
        var targetOffset = 0;
        _history.Navigating(() => move((document, offset) =>
        {
            target = document;
            targetOffset = offset;
        }));

        if (target is not null)
            _history.Record(new NavigationPoint(target.Path, targetOffset));
    }

    // ---------------------------------------------------------------- the moves

    /// <summary>A Problems row: its open document, or (a build problem) the file, opened at its line and column.</summary>
    public void ToProblem(ProblemItem problem) => Jump(arrived =>
    {
        if (problem.Document is { } document && problem.Offset >= 0)
        {
            _documents.Activate(document);
            document.Reveal(problem.Offset);
            arrived(document, problem.Offset);
            return;
        }

        if (problem.Path.Length == 0 || !File.Exists(problem.Path))
        {
            Status(problem.Path.Length == 0 ? "This problem has no file to go to." : $"'{problem.Path}' doesn't exist.");
            return;
        }

        try
        {
            var opened = _documents.OpenAndActivate(problem.Path);
            if (problem.Line <= 0)
            {
                arrived(opened, opened.CaretOffset);
                return;
            }
            var text = opened.Document;
            var line = text.GetLineByNumber(Math.Clamp(problem.Line, 1, text.LineCount));
            var offset = line.Offset + Math.Clamp(problem.Column - 1, 0, line.Length);
            opened.Reveal(offset);
            arrived(opened, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open '{problem.Path}': {ex.Message}");
        }
    });

    public void ToDebugLocation(string path, int line) => Jump(arrived =>
    {
        try
        {
            var document = _documents.OpenAndActivate(path);
            var lineNumber = Math.Clamp(line, 1, document.Document.LineCount);
            var offset = document.Document.GetLineByNumber(lineNumber).Offset;
            document.Reveal(offset);
            arrived(document, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't open the stopped source location: {ex.Message}");
        }
    });

    /// <param name="searchQuery">The query that produced the result, used to select the match.</param>
    public void ToSearchResult(WorkspaceSearchResult result, string? searchQuery) => Jump(arrived =>
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
            arrived(document, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open search result '{result.FilePath}': {ex.Message}");
        }
    });

    /// <param name="item">The outline entry to jump to (in the selected tab).</param>
    public void ToOutlineItem(DocumentOutlineItem item)
    {
        if (_documents.Active is not { } document)
            return;
        if (item.StartOffset <= document.CaretOffset && document.CaretOffset <= item.EndOffset)
            return;

        Jump(arrived =>
        {
            document.Reveal(item.NavigationOffset);
            arrived(document, item.NavigationOffset);
        });
    }

    public void ToLocation(LspLocation location)
    {
        if (FileUris.ToPath(location.Uri) is not { } localPath)
        {
            Status($"Cannot navigate to unsupported location '{location.Uri}'.");
            return;
        }

        Jump(arrived =>
        {
            try
            {
                var document = _documents.OpenAndActivate(localPath);
                var offset = document.OffsetOf(location.Range.StartLine, location.Range.StartChar);
                var endOffset = document.OffsetOf(location.Range.EndLine, location.Range.EndChar);
                document.Reveal(offset, Math.Max(0, endOffset - offset));
                arrived(document, offset);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Status($"Couldn't open location '{localPath}': {ex.Message}");
            }
        });
    }

    /// <summary>
    /// A location in a source generator's output (not on disk), with its <paramref name="text"/> from Roslyn: opened
    /// read-only as {temp}/AXamlIDE/Generated/{hint name}, as Visual Studio shows generated files.
    /// </summary>
    public void ToGeneratedLocation(LspLocation location, string text) => Jump(arrived =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AXamlIDE", "Generated", LspHelpers.SourceGeneratedName(location.Uri));
        var document = _documents.OpenReadOnly(path, text, LanguageIds.CSharp);
        if (document.Document.Text != text)
            document.Document.Text = text;   // open from an earlier visit: the generator has run since
        _documents.Activate(document);
        var offset = document.OffsetOf(location.Range.StartLine, location.Range.StartChar);
        var endOffset = document.OffsetOf(location.Range.EndLine, location.Range.EndChar);
        document.Reveal(offset, Math.Max(0, endOffset - offset));
        arrived(document, offset);
        Status($"{LspHelpers.SourceGeneratedName(location.Uri)} is source-generated (read-only).");
    });

    /// <summary>Go to a line of the active document (Ctrl+G), 1-based; recorded like any jump.</summary>
    public void ToLine(int line)
    {
        if (_documents.Active is not { } document)
            return;
        Jump(arrived =>
        {
            var text = document.Document;
            var offset = text.GetLineByNumber(Math.Clamp(line, 1, text.LineCount)).Offset;
            document.Reveal(offset);
            arrived(document, offset);
        });
    }

    private void Status(string text) => _messenger.Send(new StatusMessage(text));
}
