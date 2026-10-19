using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Problems;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Problems tool tab: every diagnostic in every open document, plus the preview's error for an AXAML file
/// (the preview only says "see the Problems tab"), and F8 / Shift+F8 navigation.
/// Rebuilds itself whenever diagnostics change, the preview reports, or a document closes.
/// </summary>
public sealed partial class ProblemsViewModel : IRecipient<DiagnosticsChangedMessage>, IRecipient<PreviewProblemMessage>
{
    private readonly DocumentManager _documents;
    private readonly IMessenger _messenger;
    private readonly ProblemNavigationService _navigation = new();
    private readonly Dictionary<OpenDocument, PreviewError> _previewErrors = [];

    public ProblemsViewModel(DocumentManager documents, IMessenger messenger)
    {
        _documents = documents;
        _messenger = messenger;

        _documents.Documents.CollectionChanged += OnDocumentsChanged;
        _messenger.RegisterAll(this);
    }

    public ObservableCollection<ProblemItem> Problems { get; } = [];

    public void Receive(DiagnosticsChangedMessage message) => Rebuild();

    public void Receive(PreviewProblemMessage message)
    {
        if (message.Error is { } error)
        {
            if (_previewErrors.TryGetValue(message.Document, out var current) && current == error)
                return;   // the same error again (every keystroke re-renders)
            _previewErrors[message.Document] = error;
        }
        else if (!_previewErrors.Remove(message.Document))
        {
            return;   // it was fine before too
        }

        Rebuild();
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>Show this problem in the editor (double-click in the list).</summary>
    [RelayCommand]
    private void OpenProblem(ProblemItem? problem)
    {
        if (problem is not null)
            _messenger.Send(new NavigateToProblemMessage(problem));
    }

    /// <summary>Jump to the next (or previous) diagnostic after the caret, across open documents.</summary>
    [RelayCommand]
    private void NavigateDiagnostic(bool forward)
    {
        var active = _documents.Active;
        var location = _navigation.FindNext(
            Problems.Select(problem => (problem.Document, problem.Offset)),
            _documents.Documents, active, active?.CaretOffset ?? 0, forward);
        if (location is not { } selected)
        {
            _messenger.Send(new StatusMessage("There are no diagnostics to navigate to."));
            return;
        }

        _messenger.Send(new ShowToolTabMessage(ToolTab.Problems));
        if (Problems.FirstOrDefault(problem =>
                ReferenceEquals(problem.Document, selected.Document)
                && problem.Offset == selected.Offset) is { } match)
            _messenger.Send(new NavigateToProblemMessage(match));
    }

    private void Rebuild()
    {
        foreach (var closed in _previewErrors.Keys.Where(document => !_documents.Documents.Contains(document)).ToList())
            _previewErrors.Remove(closed);

        Problems.Clear();
        foreach (var document in _documents.Documents)
            foreach (var diagnostic in document.Diagnostics)
            {
                var location = document.Document.GetLocation(diagnostic.StartOffset);
                Problems.Add(new ProblemItem(
                    document,
                    diagnostic.Severity,
                    location.Line,
                    diagnostic.StartOffset,
                    diagnostic.Message));
            }

        foreach (var (document, error) in _previewErrors)
        {
            var line = Math.Clamp(error.Line ?? 1, 1, document.Document.LineCount);
            var lineStart = document.Document.GetLineByNumber(line);
            var offset = lineStart.Offset + Math.Clamp((error.Column ?? 1) - 1, 0, lineStart.Length);
            Problems.Add(new ProblemItem(document, LspDiagnosticSeverity.Error, line, offset, "Preview: " + error.Message));
        }
    }
}
