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
using AXamlIDE.Services.Running;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Problems tool tab, like Visual Studio's Error List: every diagnostic in every open document, the errors and
/// warnings of the last build (in any file, open or not), and the preview's error for an AXAML file. The three
/// filter buttons (Errors / Warnings / Messages) show counts and hide a kind; F8 / Shift+F8 step through the
/// problems in open documents. Rebuilds itself whenever diagnostics change, a build finishes, the preview reports,
/// or a document opens or closes.
/// </summary>
public sealed partial class ProblemsViewModel : ObservableObject,
    IRecipient<DiagnosticsChangedMessage>, IRecipient<PreviewProblemMessage>, IRecipient<BuildProblemsMessage>
{
    private readonly DocumentManager _documents;
    private readonly IMessenger _messenger;
    private readonly ProblemNavigationService _navigation = new();
    private readonly Dictionary<OpenDocument, PreviewError> _previewErrors = [];
    private readonly List<ProblemItem> _all = [];
    private IReadOnlyList<BuildDiagnostic> _buildDiagnostics = [];

    public ProblemsViewModel(DocumentManager documents, IMessenger messenger)
    {
        _documents = documents;
        _messenger = messenger;

        _documents.Documents.CollectionChanged += OnDocumentsChanged;
        _messenger.RegisterAll(this);
    }

    /// <summary>The rows shown (after the filters).</summary>
    public ObservableCollection<ProblemItem> Problems { get; } = [];

    [ObservableProperty] public partial int ErrorCount { get; private set; }
    [ObservableProperty] public partial int WarningCount { get; private set; }
    [ObservableProperty] public partial int InfoCount { get; private set; }

    public string ErrorsLabel => $"{ErrorCount} {(ErrorCount == 1 ? "Error" : "Errors")}";
    public string WarningsLabel => $"{WarningCount} {(WarningCount == 1 ? "Warning" : "Warnings")}";
    public string InfosLabel => $"{InfoCount} {(InfoCount == 1 ? "Message" : "Messages")}";

    partial void OnErrorCountChanged(int value) => OnPropertyChanged(nameof(ErrorsLabel));
    partial void OnWarningCountChanged(int value) => OnPropertyChanged(nameof(WarningsLabel));
    partial void OnInfoCountChanged(int value) => OnPropertyChanged(nameof(InfosLabel));

    [ObservableProperty] public partial bool ShowErrors { get; set; } = true;
    [ObservableProperty] public partial bool ShowWarnings { get; set; } = true;
    [ObservableProperty] public partial bool ShowInfos { get; set; } = true;

    partial void OnShowErrorsChanged(bool value) => Refilter();
    partial void OnShowWarningsChanged(bool value) => Refilter();
    partial void OnShowInfosChanged(bool value) => Refilter();

    /// <summary>Only rows whose message, code, file or project contains this.</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Refilter();

    public void Receive(DiagnosticsChangedMessage message) => Rebuild();

    /// <summary>A build finished: its errors and warnings replace the last build's.</summary>
    public void Receive(BuildProblemsMessage message)
    {
        _buildDiagnostics = message.Diagnostics;
        Rebuild();
    }

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

    /// <summary>Show this problem in the editor (double-click or Enter in the list).</summary>
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
        var located = Problems.Where(problem => problem.Document is not null).ToList();
        var location = _navigation.FindNext(
            located.Select(problem => (problem.Document!, problem.Offset)),
            _documents.Documents, active, active?.CaretOffset ?? 0, forward);
        if (location is not { } selected)
        {
            _messenger.Send(new StatusMessage("There are no diagnostics to navigate to."));
            return;
        }

        _messenger.Send(new ShowToolTabMessage(ToolTab.Problems));
        if (located.FirstOrDefault(problem =>
                ReferenceEquals(problem.Document, selected.Document)
                && problem.Offset == selected.Offset) is { } match)
            _messenger.Send(new NavigateToProblemMessage(match));
    }

    private void Rebuild()
    {
        foreach (var closed in _previewErrors.Keys.Where(document => !_documents.Documents.Contains(document)).ToList())
            _previewErrors.Remove(closed);

        _all.Clear();
        foreach (var document in _documents.Documents)
            foreach (var diagnostic in document.Diagnostics)
            {
                var code = LspHelpers.DiagnosticCode(diagnostic.RawDiagnostic);
                _all.Add(ProblemItem.InDocument(document, diagnostic.Severity, diagnostic.StartOffset,
                    LspHelpers.DiagnosticText(diagnostic.Message, code), ProblemSource.IntelliSense, code));
            }

        // Build problems: in a file that's open, the same thing is usually already listed by its language server
        // (the compiler's message, at the same line), so those are left out. The rest are listed by file.
        var open = _documents.Documents.ToDictionary(document => document.Path, GitStatus.PathComparer);
        foreach (var diagnostic in _buildDiagnostics)
        {
            var document = diagnostic.HasLocation ? open.GetValueOrDefault(diagnostic.Path) : null;
            if (document is not null
                && _all.Any(problem => ReferenceEquals(problem.Document, document)
                                       && problem.Line == diagnostic.Line
                                       && SameText(problem.Message, diagnostic.Message)))
                continue;

            _all.Add(new ProblemItem(diagnostic.Path, diagnostic.Severity, diagnostic.Line, diagnostic.Column,
                diagnostic.Message, ProblemSource.Build, diagnostic.Code, diagnostic.Project,
                document, document is not null ? OffsetIn(document, diagnostic.Line, diagnostic.Column) : -1));
        }

        foreach (var (document, error) in _previewErrors)
        {
            var line = Math.Clamp(error.Line ?? 1, 1, document.Document.LineCount);
            _all.Add(ProblemItem.InDocument(document, LspDiagnosticSeverity.Error, OffsetIn(document, line, error.Column ?? 1),
                error.Message, ProblemSource.Preview));
        }

        ErrorCount = _all.Count(problem => problem.IsError);
        WarningCount = _all.Count(problem => problem.IsWarning);
        InfoCount = _all.Count(problem => problem.IsInfo);
        Refilter();
    }

    private void Refilter()
    {
        var filter = Filter.Trim();
        Problems.Clear();
        foreach (var problem in _all)
        {
            var shown = problem.IsError ? ShowErrors : problem.IsWarning ? ShowWarnings : ShowInfos;
            if (!shown)
                continue;
            if (filter.Length > 0
                && !Contains(problem.Message, filter) && !Contains(problem.Code, filter)
                && !Contains(problem.FileName, filter) && !Contains(problem.ProjectName, filter))
                continue;
            Problems.Add(problem);
        }
    }

    private static int OffsetIn(OpenDocument document, int line, int column)
    {
        var text = document.Document;
        var lineStart = text.GetLineByNumber(Math.Clamp(line, 1, text.LineCount));
        return lineStart.Offset + Math.Clamp(column - 1, 0, lineStart.Length);
    }

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>The compiler's message and the language server's, which can differ only in trailing punctuation.</summary>
    private static bool SameText(string a, string b)
        => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.Ordinal);
}
