using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Indentation;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The status bar along the bottom of the main window, like Rider's and Visual Studio's. From left to right:
///  - the last message (<see cref="StatusMessage"/>), which goes back to "Ready" after a while, as in Visual Studio;
///    its tooltip lists the recent ones, so a message that went by too fast can still be read;
///  - solution-wide analysis: "4 errors in 3 files" (Rider), or that it's off; click for its menu;
///  - the active document: its errors and warnings, Ln/Col, line endings, encoding and indentation;
///  - the language server: an icon while it's running, what it's doing while it's busy, why it stopped if it did.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject,
    IRecipient<StatusMessage>, IRecipient<EditorStateChangedMessage>,
    IRecipient<DiagnosticsChangedMessage>, IRecipient<SolutionDiagnosticsChangedMessage>
{
    private const string ReadyText = "Ready";

    /// <summary>How long a message stays before the status bar goes back to "Ready".</summary>
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(10);

    /// <summary>How many messages the tooltip keeps.</summary>
    private const int HistoryLength = 15;

    /// <summary>The editor's indent width: AvaloniaEdit's default, which the editor doesn't change.</summary>
    private const int IndentationSize = 4;

    private readonly DocumentManager _documents;
    private readonly LspManager _servers;
    private readonly SolutionAnalysisService _solutionAnalysis;
    private readonly SettingsViewModel _settings;
    private readonly ProblemsViewModel _problems;
    private readonly IMessenger _messenger;
    private readonly DispatcherTimer _messageTimer;
    private readonly List<(DateTime Time, string Text)> _history = [];
    private bool _activeUsesTabs;

    public StatusBarViewModel(DocumentManager documents, LspManager servers, SolutionAnalysisService solutionAnalysis,
        SettingsViewModel settings, ProblemsViewModel problems, IMessenger messenger)
    {
        _documents = documents;
        _servers = servers;
        _solutionAnalysis = solutionAnalysis;
        _settings = settings;
        _problems = problems;
        _messenger = messenger;

        _messageTimer = new DispatcherTimer { Interval = MessageLifetime };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            MessageText = ReadyText;
        };

        _documents.ActiveChanged += OnActiveDocumentChanged;
        _servers.StatusChanged += UpdateLanguageServer;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SolutionWideAnalysis))
                UpdateSolutionAnalysis();
        };
        messenger.RegisterAll(this);

        UpdateLanguageServer();
        UpdateSolutionAnalysis();
    }

    // ---------------------------------------------------------------- the message

    [ObservableProperty] public partial string MessageText { get; private set; } = ReadyText;

    /// <summary>The recent messages, newest first, with their times.</summary>
    [ObservableProperty] public partial string MessageHistory { get; private set; } = "No messages yet.";

    /// <summary>Show a message; it goes back to "Ready" after <see cref="MessageLifetime"/>.</summary>
    public void Show(string text)
    {
        MessageText = text;
        _messageTimer.Stop();
        _messageTimer.Start();

        _history.Insert(0, (DateTime.Now, text));
        if (_history.Count > HistoryLength)
            _history.RemoveAt(_history.Count - 1);
        MessageHistory = string.Join(Environment.NewLine, _history.Select(item => $"{item.Time:HH:mm:ss}  {item.Text}"));
    }

    public void Receive(StatusMessage message) => Show(message.Text);

    // ---------------------------------------------------------------- the active document

    [ObservableProperty] public partial bool HasDocument { get; private set; }
    [ObservableProperty] public partial string DocumentProblemsText { get; private set; } = "";
    [ObservableProperty] public partial string CaretText { get; private set; } = "";
    [ObservableProperty] public partial string LineEndingText { get; private set; } = "";
    [ObservableProperty] public partial string EncodingText { get; private set; } = "";
    [ObservableProperty] public partial string IndentText { get; private set; } = "";

    /// <summary>Caret moved or diagnostics changed in the editor.</summary>
    public void Receive(EditorStateChangedMessage message) => UpdateDocument();

    private void OnActiveDocumentChanged(OpenDocument? document)
    {
        // Worked out once per tab switch (it reads the whole text), the same way the editor picks tabs or spaces.
        _activeUsesTabs = document is not null && IndentDetection.UsesTabs(document.Document.Text);
        UpdateDocument();
        UpdateSolutionAnalysis();   // the active file's errors count too
    }

    private void UpdateDocument()
    {
        if (_documents.Active is not { } document)
        {
            HasDocument = false;
            DocumentProblemsText = CaretText = LineEndingText = EncodingText = IndentText = "";
            return;
        }

        var text = document.Document;
        var caret = text.GetLocation(Math.Min(document.CaretOffset, text.TextLength));
        HasDocument = true;
        CaretText = $"Ln {caret.Line}, Col {caret.Column}";
        DocumentProblemsText = $"{Count(document.ErrorCount, "error")}, {Count(document.WarningCount, "warning")}";
        LineEndingText = LineEnding(document);
        EncodingText = EncodingName(document.Encoding);
        IndentText = _activeUsesTabs ? "Tabs" : $"{IndentationSize} spaces";
    }

    /// <summary>The first line's ending: "LF" (Linux, macOS), "CRLF" (Windows) or "CR". A one-line file has none yet,
    /// so it gets this machine's.</summary>
    private static string LineEnding(OpenDocument document)
    {
        var text = document.Document;
        var first = text.GetLineByNumber(1);
        return first.DelimiterLength switch
        {
            2 => "CRLF",
            1 => text.GetCharAt(first.EndOffset) == '\r' ? "CR" : "LF",
            _ => Environment.NewLine == "\r\n" ? "CRLF" : "LF"
        };
    }

    /// <summary>"UTF-8", "UTF-8 with BOM", "UTF-16 LE"... as VS Code names them.</summary>
    private static string EncodingName(Encoding encoding) => encoding switch
    {
        UTF8Encoding => encoding.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8",
        UnicodeEncoding => encoding.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE",
        _ => encoding.WebName.ToUpperInvariant()
    };

    // ---------------------------------------------------------------- solution-wide analysis

    [ObservableProperty] public partial bool IsSolutionAnalysisOn { get; private set; }

    /// <summary>On, with errors somewhere (the icon is the error one).</summary>
    [ObservableProperty] public partial bool SolutionHasErrors { get; private set; }

    /// <summary>On, analysed, and no errors anywhere (the icon is a tick).</summary>
    [ObservableProperty] public partial bool SolutionIsClean { get; private set; }

    /// <summary>Off, or still waiting for results (the icon is a plain circle).</summary>
    public bool SolutionIsPending => !SolutionHasErrors && !SolutionIsClean;

    partial void OnSolutionHasErrorsChanged(bool value) => OnPropertyChanged(nameof(SolutionIsPending));
    partial void OnSolutionIsCleanChanged(bool value) => OnPropertyChanged(nameof(SolutionIsPending));

    /// <summary>"4 errors in 3 files", "No errors", "Analysing..."; empty when off (just the icon shows).</summary>
    [ObservableProperty] public partial string SolutionAnalysisText { get; private set; } = "";

    [ObservableProperty] public partial string SolutionAnalysisTip { get; private set; } = "";

    public void Receive(DiagnosticsChangedMessage message) => UpdateSolutionAnalysis();

    public void Receive(SolutionDiagnosticsChangedMessage message) => UpdateSolutionAnalysis();

    /// <summary>Tools > Solution-Wide Analysis, and the status bar's menu: switch it on or off (saved in the settings).</summary>
    [RelayCommand]
    private void ToggleSolutionAnalysis() => _settings.SolutionWideAnalysis = !_settings.SolutionWideAnalysis;

    /// <summary>Show every error in the solution: the Problems list, with the Entire Solution scope.</summary>
    [RelayCommand]
    private void ShowSolutionErrors()
    {
        _problems.Scope = ProblemScope.EntireSolution;
        _messenger.Send(new ShowToolTabMessage(ToolTab.Problems));
    }

    private void UpdateSolutionAnalysis()
    {
        IsSolutionAnalysisOn = _settings.SolutionWideAnalysis;
        if (!IsSolutionAnalysisOn)
        {
            SolutionHasErrors = SolutionIsClean = false;
            SolutionAnalysisText = "";
            SolutionAnalysisTip = "Solution-wide analysis is off: only open files are checked as you type. Click to turn it on.";
            return;
        }

        if (!_solutionAnalysis.HasResults)
        {
            SolutionHasErrors = SolutionIsClean = false;
            SolutionAnalysisText = "Analysing solution…";
            SolutionAnalysisTip = "Solution-wide analysis is on and waiting for the language server's first results.";
            return;
        }

        // Open files: their own (live) diagnostics. The rest: what the solution analysis last reported.
        var errorsByFile = _documents.Documents
            .Where(document => document.ErrorCount > 0)
            .ToDictionary(document => document.Path, document => document.ErrorCount, GitStatus.PathComparer);
        foreach (var (path, diagnostics) in _solutionAnalysis.Files)
        {
            if (_documents.Documents.Any(document => GitStatus.PathComparer.Equals(document.Path, path)))
                continue;
            var errors = diagnostics.Count(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
            if (errors > 0)
                errorsByFile[path] = errors;
        }

        var total = errorsByFile.Values.Sum();
        SolutionHasErrors = total > 0;
        SolutionIsClean = total == 0;
        SolutionAnalysisText = total == 0
            ? "No errors"
            : $"{Count(total, "error")} in {Count(errorsByFile.Count, "file")}";
        SolutionAnalysisTip = "Solution-wide analysis: every file in the solution is checked. Click for the errors, or to turn it off.";
    }

    // ---------------------------------------------------------------- the language server

    /// <summary>A server has been started for this workspace: the indicator shows.</summary>
    [ObservableProperty] public partial bool ShowLanguageServer { get; private set; }

    /// <summary>A server couldn't start, or stopped.</summary>
    [ObservableProperty] public partial bool LanguageServerHasProblem { get; private set; }

    /// <summary>What it's doing while busy ("Loading projects..."), or why it isn't working; empty when it's just running.</summary>
    [ObservableProperty] public partial string LanguageServerText { get; private set; } = "";

    [ObservableProperty] public partial string LanguageServerTip { get; private set; } = "";

    private void UpdateLanguageServer()
    {
        var summary = _servers.Summary;
        ShowLanguageServer = summary.AnyStarted;
        LanguageServerHasProblem = summary.ProblemText is not null;
        LanguageServerText = summary.ProblemText ?? summary.BusyText ?? "";
        LanguageServerTip = summary.Details.Length > 0
            ? $"C# language server{Environment.NewLine}{summary.Details}"
            : "C# language server";
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
