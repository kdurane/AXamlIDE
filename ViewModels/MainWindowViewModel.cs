using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Commands;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Snippets;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The main window's view model: the one main UI (menus, top bar, activity bar, status bar), which acts
/// on the selected tab wherever it is (main window or a floating one). The tabs and tool panels are in
/// <see cref="Docking"/>; each panel has its own view model. This class owns what belongs to the app as a
/// whole: the status bar text and workspace-wide commands.
///
/// Handles <see cref="StatusMessage"/> and <see cref="EditorStateChangedMessage"/> from any area.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject,
    IRecipient<StatusMessage>, IRecipient<EditorStateChangedMessage>, IRecipient<OpenWorkspaceMessage>
{
    private readonly DocumentManager _documents;
    private readonly DocumentSaveService _saver;
    private readonly LanguageSessionService _languages;
    private readonly SettingsViewModel _settings;
    private readonly IDialogService _dialogs;
    private readonly SnippetLibrary _snippets;
    private readonly IMessenger _messenger;

    public MainWindowViewModel(
        DocumentManager documents,
        DocumentSaveService saver,
        DocumentCommands documentCommands,
        LanguageSessionService languages,
        WorkspaceViewModel workspace,
        ExplorerViewModel explorer,
        DockLayoutViewModel docking,
        WorkspaceSession session,
        SearchViewModel search,
        ProblemsViewModel problems,
        RunViewModel run,
        TerminalViewModel terminal,
        TestExplorerViewModel tests,
        StartViewModel start,
        PropertiesViewModel properties,
        GitViewModel git,
        NuGetManager nuGet,
        PublishManager publish,
        VisualizerManager visualizers,
        EditorCommands editorCommands,
        SettingsViewModel settings,
        EditorSettingsLoadResult settingsLoad,
        SnippetLibrary snippets,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _snippets = snippets;
        _documents = documents;
        _saver = saver;
        _languages = languages;
        _settings = settings;
        _dialogs = dialogs;
        _messenger = messenger;
        DocumentCommands = documentCommands;
        EditorCommands = editorCommands;
        Workspace = workspace;
        Explorer = explorer;
        Docking = docking;
        Session = session;
        Search = search;
        Problems = problems;
        Run = run;
        Terminal = terminal;
        Tests = tests;
        Start = start;
        Properties = properties;
        Git = git;
        NuGet = nuGet;
        Publish = publish;
        Visualizers = visualizers;

        _documents.ActiveChanged += _ => UpdateEditorStatus();
        messenger.RegisterAll(this);

        if (settingsLoad.Warnings.Count > 0)
            ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));
    }

    // ---------------------------------------------------------------- panels

    /// <summary>The document tabs and tool panels, in every window.</summary>
    public DockLayoutViewModel Docking { get; }

    /// <summary>The open workspace's remembered session (open files, layout). MainWindow saves it on exit.</summary>
    public WorkspaceSession Session { get; }

    /// <summary>Find in files (the top bar's search box sends its query here).</summary>
    public SearchViewModel Search { get; }

    /// <summary>The top bar's search box text.</summary>
    [ObservableProperty] public partial string TopBarSearchText { get; set; } = "";

    // Areas the menus and KeyMap bind to.
    public WorkspaceViewModel Workspace { get; }
    public ExplorerViewModel Explorer { get; }
    public ProblemsViewModel Problems { get; }
    public RunViewModel Run { get; }
    public TerminalViewModel Terminal { get; }
    public TestExplorerViewModel Tests { get; }

    /// <summary>The start window's actions and the recent list (File > New Project, Clone, Open, Recent).</summary>
    public StartViewModel Start { get; }
    public PropertiesViewModel Properties { get; }
    public GitViewModel Git { get; }
    public NuGetManager NuGet { get; }
    public PublishManager Publish { get; }

    /// <summary>Opens debugger visualizer tabs (created with the shell so it listens from the start).</summary>
    public VisualizerManager Visualizers { get; }

    /// <summary>Save / close / format, acting on the selected tab wherever it is.</summary>
    public DocumentCommands DocumentCommands { get; }

    /// <summary>Editor actions (go to definition, rename...) for menus and key bindings.</summary>
    public EditorCommands EditorCommands { get; }

    // ---------------------------------------------------------------- window state

    [ObservableProperty] public partial string StatusText { get; set; } = "language servers: idle";
    [ObservableProperty] public partial string CaretText { get; set; } = "";
    [ObservableProperty] public partial string DiagnosticsText { get; set; } = "";

    public void ShowStatusMessage(string message) => StatusText = message;

    public void Receive(StatusMessage message) => ShowStatusMessage(message.Text);

    /// <summary>Caret moved or diagnostics changed in the editor.</summary>
    public void Receive(EditorStateChangedMessage message) => UpdateEditorStatus();

    private void UpdateEditorStatus()
    {
        if (_documents.Active is not { } document)
        {
            CaretText = DiagnosticsText = "";
            return;
        }

        var caret = document.Document.GetLocation(Math.Min(document.CaretOffset, document.Document.TextLength));
        CaretText = $"Ln {caret.Line}, Col {caret.Column}";
        DiagnosticsText = $"{document.ErrorCount} errors, {document.WarningCount} warnings";
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Switch to the Search panel and put the cursor in the find box.</summary>
    [RelayCommand]
    private void ShowFindInFiles()
    {
        Docking.ShowTool(ToolTab.Search);
        _messenger.Send(new FocusFindInFilesMessage());
    }

    /// <summary>The top bar's search box: search the workspace for its text, in the Search window.</summary>
    [RelayCommand]
    private void SearchFromTopBar()
    {
        if (string.IsNullOrWhiteSpace(TopBarSearchText))
        {
            ShowFindInFiles();
            return;
        }

        Search.SearchQuery = TopBarSearchText;
        Docking.ShowTool(ToolTab.Search);
        Search.SearchWorkspaceCommand.Execute(null);
    }

    [RelayCommand]
    private void ToggleExplorer() => Docking.ToggleExplorer();

    /// <summary>VS Code's Toggle Panel (Ctrl+J): hide or show the bottom panel (Problems, Output, Terminal...).</summary>
    [RelayCommand]
    private void TogglePanel() => Docking.TogglePanel();

    /// <summary>View > Terminal (Ctrl+`): show the Terminal and put the cursor on its command line.</summary>
    [RelayCommand]
    private void ShowTerminal()
    {
        Docking.ShowTool(ToolTab.Terminal);
        Terminal.RequestFocus();
    }

    /// <summary>The View menu: bring a tool panel (a <see cref="ToolTab"/> id) to the front, putting it back if closed.</summary>
    [RelayCommand]
    private void ShowTool(string id) => Docking.ShowTool(id);

    /// <summary>File > Exit: closes the main window (which asks about unsaved changes and saves the session).</summary>
    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    /// <summary>Raised by <see cref="ExitCommand"/>; MainWindow closes itself.</summary>
    public event Action? ExitRequested;

    /// <summary>File > Start Window: the start window over this one (it opens what's chosen in it).</summary>
    [RelayCommand]
    private Task ShowStartWindow() => _dialogs.ShowStartWindowAsync(Start);

    /// <summary>Ask about unsaved changes, then close every document and the workspace.</summary>
    [RelayCommand]
    private Task CloseSolution() => CloseWorkspaceAsync();

    /// <summary>Open a solution or folder from the start window, the recent list, a clone or a new project.</summary>
    public async void Receive(OpenWorkspaceMessage message) => await OpenWorkspaceAsync(message.Path);

    /// <summary>Close what's open (asking about unsaved changes; false if the user cancels), then open <paramref name="path"/>.</summary>
    public async Task<bool> OpenWorkspaceAsync(string path)
    {
        if (Workspace.IsOpen && !await CloseWorkspaceAsync())
            return false;
        return await Workspace.OpenAsync(path);
    }

    /// <summary>Ask about unsaved changes, then close every document and the workspace. False if the user cancelled or a save failed.</summary>
    private async Task<bool> CloseWorkspaceAsync()
    {
        var dirtyDocuments = _documents.Documents.Where(document => document.IsDirty).ToArray();
        var decisions = new List<(OpenDocument Document, SaveChoice Choice)>(dirtyDocuments.Length);
        foreach (var document in dirtyDocuments)
        {
            var choice = await _dialogs.AskSaveAsync(document);
            if (choice == SaveChoice.Cancel) return false;
            decisions.Add((document, choice));
        }

        foreach (var (document, choice) in decisions)
            if (choice == SaveChoice.Save && !await _saver.SaveAsync(document))
                return false;   // a save failed: keep everything open

        if (Run.IsDebugging) Run.StopRunningProject();

        Session.Close();   // remember what was open before closing it
        foreach (var document in _documents.Documents.ToArray())
            _documents.Close(document, force: true);
        Docking.CloseAllPages();

        _documents.Active = null;
        Workspace.Close();
        return true;
    }

    [RelayCommand]
    private void RestartLanguageServices() => _languages.Restart();

    [RelayCommand]
    private void ToggleSolutionExplorerSide()
        => _settings.SolutionExplorerOnRight = !_settings.SolutionExplorerOnRight;

    [RelayCommand]
    private Task OpenSettings() => _dialogs.ShowSettingsAsync();

    /// <summary>
    /// Tools > Edit Snippets... (VS Code's Configure User Snippets): open the user's snippet file for a language,
    /// made with an example first. Saved changes are picked up on the next completion.
    /// </summary>
    [RelayCommand]
    private async Task EditSnippets()
    {
        var languages = new Dictionary<string, string> { ["C#"] = LanguageIds.CSharp, ["AXAML"] = LanguageIds.Axaml };
        if (await _dialogs.PickAsync("Edit Snippets", "Language", [.. languages.Keys]) is not { } language)
            return;
        try
        {
            var path = _snippets.EnsureUserFile(languages[language]);
            _documents.Active = _documents.Open(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowStatusMessage($"Couldn't open the snippet file: {ex.Message}");
        }
    }
}
