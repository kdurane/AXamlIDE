using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Problems;
using AXamlIDE.Services.Running;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Problems tool tab, like Visual Studio's Error List: every diagnostic in every open document, the errors and
/// warnings of the last build (in any file, open or not), with solution-wide analysis on the live errors of every
/// file in the solution, and the preview's error for an AXAML file. The scope box
/// narrows the list to the open documents, the active document's project, or the active document; the three
/// filter buttons (Errors / Warnings / Messages) show the counts within that scope and hide a kind; F8 / Shift+F8
/// step through the problems in open documents. Rebuilds itself whenever diagnostics change, a build finishes, the
/// preview reports, or a document opens or closes.
/// </summary>
public sealed partial class ProblemsViewModel : ObservableObject,
    IRecipient<DiagnosticsChangedMessage>, IRecipient<SolutionDiagnosticsChangedMessage>,
    IRecipient<PreviewProblemMessage>, IRecipient<BuildProblemsMessage>
{
    private readonly DocumentManager _documents;
    private readonly WorkspaceModel _workspace;
    private readonly SolutionAnalysisService _solutionAnalysis;
    private readonly IMessenger _messenger;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly ProblemNavigationService _navigation = new();
    private readonly Dictionary<OpenDocument, PreviewError> _previewErrors = [];
    private readonly List<ProblemItem> _all = [];
    private IReadOnlyList<BuildDiagnostic> _buildDiagnostics = [];

    // The project that owns each file, found once per rebuild (the solution can change between rebuilds).
    private readonly Dictionary<string, string?> _projectOfFile = new(GitStatus.PathComparer);

    public ProblemsViewModel(DocumentManager documents, WorkspaceModel workspace, SolutionAnalysisService solutionAnalysis,
        IMessenger messenger, IClipboardService clipboard, IDialogService dialogs)
    {
        _clipboard = clipboard;
        _dialogs = dialogs;
        _solutionAnalysis = solutionAnalysis;
        _documents = documents;
        _workspace = workspace;
        _messenger = messenger;

        _documents.Documents.CollectionChanged += OnDocumentsChanged;
        _documents.ActiveChanged += OnActiveDocumentChanged;
        _messenger.RegisterAll(this);
    }

    /// <summary>The rows shown (after the filters).</summary>
    public ObservableCollection<ProblemItem> Problems { get; } = [];

    /// <summary>Every problem, whatever the scope and filters (the MCP server's get_problems).</summary>
    public IReadOnlyList<ProblemItem> All => _all;

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

    /// <summary>The choices in the scope box (<see cref="ProblemScope"/>).</summary>
    public IReadOnlyList<string> Scopes { get; } = ProblemScope.All;

    /// <summary>Which problems are listed and counted: one of <see cref="ProblemScope"/>.</summary>
    [ObservableProperty] public partial string Scope { get; set; } = ProblemScope.EntireSolution;

    partial void OnScopeChanged(string value) => Refilter();

    /// <summary>Only rows whose message, code, file or project contains this.</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Refilter();

    public void Receive(DiagnosticsChangedMessage message) => Rebuild();

    public void Receive(SolutionDiagnosticsChangedMessage message) => Rebuild();

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

    /// <summary>Another tab became active: the current document (or project) scopes now mean something else.</summary>
    private void OnActiveDocumentChanged(OpenDocument? document)
    {
        if (Scope is ProblemScope.CurrentDocument or ProblemScope.CurrentProject)
            Refilter();
    }

    /// <summary>Show this problem in the editor (double-click or Enter in the list).</summary>
    [RelayCommand]
    private void OpenProblem(ProblemItem? problem)
    {
        if (problem is not null)
            _messenger.Send(new NavigateToProblemMessage(problem));
    }

    /// <summary>Visual Studio's Copy (Ctrl+C): just the description, the part people paste into a search or a chat.</summary>
    [RelayCommand]
    private Task CopyProblem(ProblemItem? problem)
        => problem is null ? Task.CompletedTask : _clipboard.SetTextAsync(problem.Message);

    /// <summary>Visual Studio's Copy Row (Ctrl+Shift+C): every column, tab-separated under a header line.</summary>
    [RelayCommand]
    private Task CopyProblemRow(ProblemItem? problem)
    {
        if (problem is null)
            return Task.CompletedTask;
        var severity = problem.IsError ? "Error" : problem.IsWarning ? "Warning" : "Message";
        var text = new StringBuilder()
            .AppendLine("Severity\tCode\tDescription\tProject\tFile\tLine\tSource")
            .AppendLine(string.Join('\t', severity, problem.Code, problem.Message, problem.ProjectName, problem.Path,
                problem.LineText, problem.Source));
        return _clipboard.SetTextAsync(text.ToString());
    }

    /// <summary>Visual Studio's Search the Web for Help (Ctrl+F1): the code and description in a Bing search.</summary>
    [RelayCommand]
    private Task SearchWebForProblem(ProblemItem? problem)
    {
        if (problem is null)
            return Task.CompletedTask;
        var query = problem.Code.Length > 0 ? $"{problem.Code} {problem.Message}" : problem.Message;
        return _dialogs.OpenUrlAsync("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));
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
        _projectOfFile.Clear();
        foreach (var document in _documents.Documents)
            foreach (var diagnostic in document.Diagnostics)
            {
                if (PreviewErrorDiagnostic.IsFrom(diagnostic.RawDiagnostic))
                    continue;   // the preview's error: listed below as the preview's own row
                var code = LspHelpers.DiagnosticCode(diagnostic.RawDiagnostic);
                _all.Add(ProblemItem.InDocument(document, diagnostic.Severity, diagnostic.StartOffset,
                    LspHelpers.DiagnosticText(diagnostic.Message, code), ProblemSource.IntelliSense, code,
                    ProjectOf(document.Path)));
            }

        // Solution-wide analysis: files that aren't open (an open file's come from its own pulls, above, which see the
        // unsaved text). Errors, warnings and messages only: a closed file's unused usings would bury the rest.
        var open = _documents.Documents.ToDictionary(document => document.Path, GitStatus.PathComparer);
        foreach (var (path, diagnostics) in _solutionAnalysis.Files)
        {
            if (open.ContainsKey(path))
                continue;
            var project = ProjectOf(path);
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity >= LspDiagnosticSeverity.Hint)
                    continue;
                var code = LspHelpers.DiagnosticCode(diagnostic.Raw);
                _all.Add(new ProblemItem(path, diagnostic.Severity, diagnostic.StartLine + 1, diagnostic.StartChar + 1,
                    LspHelpers.DiagnosticText(diagnostic.Message, code), ProblemSource.IntelliSense, code, project));
            }
        }

        // Build problems: the same thing is usually already listed by the language server (the compiler's message, at
        // the same line) for open files, and for every file with solution-wide analysis on, so those are left out.
        foreach (var diagnostic in _buildDiagnostics)
        {
            var document = diagnostic.HasLocation ? open.GetValueOrDefault(diagnostic.Path) : null;
            if (diagnostic.HasLocation
                && _all.Any(problem => problem.Source == ProblemSource.IntelliSense
                                       && problem.Line == diagnostic.Line
                                       && GitStatus.PathComparer.Equals(problem.Path, diagnostic.Path)
                                       && SameText(problem.Message, diagnostic.Message)))
                continue;

            _all.Add(new ProblemItem(diagnostic.Path, diagnostic.Severity, diagnostic.Line, diagnostic.Column,
                diagnostic.Message, ProblemSource.Build, diagnostic.Code, diagnostic.Project ?? ProjectOf(diagnostic.Path),
                document, document is not null ? OffsetIn(document, diagnostic.Line, diagnostic.Column) : -1));
        }

        foreach (var (document, error) in _previewErrors)
        {
            var line = Math.Clamp(error.Line ?? 1, 1, document.Document.LineCount);
            _all.Add(ProblemItem.InDocument(document, LspDiagnosticSeverity.Error, OffsetIn(document, line, error.Column ?? 1),
                error.Message, ProblemSource.Preview, project: ProjectOf(document.Path)));
        }

        Refilter();
    }

    /// <summary>
    /// Shows the rows that pass the scope, the three kind buttons and the text filter. The counts on the buttons
    /// are of the rows in scope, so hiding a kind doesn't zero its own count (as in Visual Studio).
    /// </summary>
    private void Refilter()
    {
        var inScope = _all.Where(InScope()).ToList();
        ErrorCount = inScope.Count(problem => problem.IsError);
        WarningCount = inScope.Count(problem => problem.IsWarning);
        InfoCount = inScope.Count(problem => problem.IsInfo);

        var filter = Filter.Trim();
        Problems.Clear();
        foreach (var problem in inScope)
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

    /// <summary>The test for "is this problem in the chosen scope?".</summary>
    private Func<ProblemItem, bool> InScope()
    {
        var active = _documents.Active;
        switch (Scope)
        {
            case ProblemScope.OpenDocuments:
                return problem => problem.Document is not null;

            case ProblemScope.CurrentDocument:
                return problem => active is not null && ReferenceEquals(problem.Document, active);

            case ProblemScope.CurrentProject:
                // A file outside every project (or no file open): nothing is listed, as in Visual Studio.
                if (active is null || ProjectOf(active.Path) is not { } project)
                    return _ => false;
                return problem => problem.Project is { } owner && GitStatus.PathComparer.Equals(owner, project);

            default:
                return _ => true;
        }
    }

    /// <summary>The .csproj that owns a file (the innermost one), or null.</summary>
    private string? ProjectOf(string path)
    {
        if (path.Length == 0)
            return null;
        if (_projectOfFile.TryGetValue(path, out var known))
            return known;

        string? project;
        try
        {
            project = _workspace.FindProjectForDocument(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            project = null;   // its folder is gone (deleted outside the IDE) or can't be read
        }

        _projectOfFile[path] = project;
        return project;
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
