using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Problems;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Viewmodels;

/// <summary>
/// The Problems tool tab: every diagnostic in every open document, and F8 / Shift+F8 navigation.
/// Rebuilds itself whenever diagnostics change or a document closes.
/// </summary>
public sealed partial class ProblemsViewModel : IRecipient<DiagnosticsChangedMessage>
{
    private readonly DocumentManager _documents;
    private readonly IMessenger _messenger;
    private readonly ProblemNavigationService _navigation = new();

    public ProblemsViewModel(DocumentManager documents, IMessenger messenger)
    {
        _documents = documents;
        _messenger = messenger;

        _documents.Documents.CollectionChanged += OnDocumentsChanged;
        _messenger.RegisterAll(this);
    }

    public ObservableCollection<ProblemItem> Problems { get; } = [];

    public void Receive(DiagnosticsChangedMessage message) => Rebuild();

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
        Problems.Clear();
        foreach (var document in _documents.Documents)
            foreach (var diagnostic in document.Diagnostics)
            {
                var location = document.Document.GetLocation(diagnostic.StartOffset);
                Problems.Add(new ProblemItem(
                    document,
                    diagnostic.Severity switch
                    {
                        LspDiagnosticSeverity.Error => "Error",
                        LspDiagnosticSeverity.Warning => "Warning",
                        _ => "Info"
                    },
                    location.Line,
                    diagnostic.StartOffset,
                    diagnostic.Message));
            }
    }
}
