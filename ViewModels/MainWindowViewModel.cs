using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Commands;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Snippets;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The main window's view model: the one main UI (menus, top bar, activity bar, status bar), which acts
/// on the selected tab wherever it is (main window or a floating one). The tabs and tool panels are in
/// <see cref="Docking"/>; each panel has its own view model. This class owns what belongs to the app as a
/// whole: workspace-wide commands; the status bar is <see cref="StatusBar"/>.
///
/// <see cref="StatusMessage"/>s from any area are shown by <see cref="StatusBar"/>.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject,
    IRecipient<OpenWorkspaceMessage>
{
    private readonly DocumentManager _documents;
    private readonly DocumentSaveService _saver;
    private readonly LanguageSessionService _languages;
    private readonly SettingsViewModel _settings;
    private readonly IDialogService _dialogs;
    private readonly SnippetLibrary _snippets;
    private readonly NavigationService _navigation;
    private readonly WorkspaceTrust _trust;
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
        SearchEverywhereViewModel searchEverywhere,
        ProblemsViewModel problems,
        RunViewModel run,
        TerminalViewModel terminal,
        TestExplorerViewModel tests,
        AgentChatViewModel claude,
        StartViewModel start,
        PropertiesViewModel properties,
        GitViewModel git,
        BreakpointsViewModel breakpoints,
        NuGetManager nuGet,
        PublishManager publish,
        VisualizerManager visualizers,
        StatusBarViewModel statusBar,
        EditorCommands editorCommands,
        NavigationService navigation,
        SettingsViewModel settings,
        EditorSettingsLoadResult settingsLoad,
        SnippetLibrary snippets,
        WorkspaceTrust trust,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _trust = trust;
        _snippets = snippets;
        _navigation = navigation;
        _documents = documents;
        _saver = saver;
        _languages = languages;
        _settings = settings;
        _dialogs = dialogs;
        _messenger = messenger;
        DocumentCommands = documentCommands;
        Breakpoints = breakpoints;
        EditorCommands = editorCommands;
        Workspace = workspace;
        Explorer = explorer;
        Docking = docking;
        Session = session;
        Search = search;
        SearchEverywhere = searchEverywhere;
        searchEverywhere.FindInFiles = FindInFiles;
        searchEverywhere.OpenSettingsAt = (page, option) => _ = _dialogs.ShowSettingsAsync(page, option);
        Problems = problems;
        Run = run;
        Terminal = terminal;
        Tests = tests;
        Claude = claude;
        Start = start;
        Properties = properties;
        Git = git;
        NuGet = nuGet;
        Publish = publish;
        Visualizers = visualizers;
        StatusBar = statusBar;

        messenger.RegisterAll(this);

        if (settingsLoad.Warnings.Count > 0)
            ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));
    }

    // ---------------------------------------------------------------- panels

    /// <summary>The document tabs and tool panels, in every window.</summary>
    public DockLayoutViewModel Docking { get; }

    /// <summary>The open workspace's remembered session (open files, layout). MainWindow saves it on exit.</summary>
    public WorkspaceSession Session { get; }

    /// <summary>Find in files (Search Everywhere's Text tab sends its words here).</summary>
    public SearchViewModel Search { get; }

    /// <summary>The top bar's box: Search Everywhere (double Shift), as Rider's.</summary>
    public SearchEverywhereViewModel SearchEverywhere { get; }

    // Areas the menus and KeyMap bind to.
    public WorkspaceViewModel Workspace { get; }
    public ExplorerViewModel Explorer { get; }
    public ProblemsViewModel Problems { get; }
    public RunViewModel Run { get; }
    public TerminalViewModel Terminal { get; }
    public TestExplorerViewModel Tests { get; }

    /// <summary>The Claude panel (View > Claude, New Claude Chat).</summary>
    public AgentChatViewModel Claude { get; }

    /// <summary>The start window's actions and the recent list (File > New Project, Clone, Open, Recent).</summary>
    public StartViewModel Start { get; }
    public PropertiesViewModel Properties { get; }
    public GitViewModel Git { get; }

    /// <summary>Every breakpoint, in open and closed files (the Debug menu's Delete All and Enable / Disable All).</summary>
    public BreakpointsViewModel Breakpoints { get; }
    public NuGetManager NuGet { get; }
    public PublishManager Publish { get; }

    /// <summary>Opens debugger visualizer tabs (created with the shell so it listens from the start).</summary>
    public VisualizerManager Visualizers { get; }

    /// <summary>Save / close / format, acting on the selected tab wherever it is.</summary>
    public DocumentCommands DocumentCommands { get; }

    /// <summary>Editor actions (go to definition, rename...) for menus and key bindings.</summary>
    public EditorCommands EditorCommands { get; }

    // ---------------------------------------------------------------- window state

    /// <summary>The status bar: messages, the active document's details, solution-wide analysis, the language server.</summary>
    public StatusBarViewModel StatusBar { get; }

    public void ShowStatusMessage(string message) => StatusBar.Show(message);

    // ---------------------------------------------------------------- commands

    /// <summary>Switch to the Search panel and put the cursor in the find box.</summary>
    [RelayCommand]
    private void ShowFindInFiles()
    {
        Docking.ShowTool(ToolTab.Search);
        _messenger.Send(new FocusFindInFilesMessage());
    }

    /// <summary>Search Everywhere's "Find ... in Files": search the workspace for the words, in the Search window.</summary>
    private void FindInFiles(string query)
    {
        Search.SearchQuery = query;
        Docking.ShowTool(ToolTab.Search);
        Search.SearchWorkspaceCommand.Execute(null);
    }

    /// <summary>Navigate Backward / Forward (Ctrl+-, Ctrl+Shift+-), as in Visual Studio.</summary>
    [RelayCommand]
    private void NavigateBack() => _navigation.Back();

    [RelayCommand]
    private void NavigateForward() => _navigation.Forward();

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

    /// <summary>
    /// View > Claude (Ctrl+Alt+I): open the Claude tab (or bring it to the front, in whichever window it's in) and put
    /// the cursor in its prompt box, like VS Code's chat.
    /// </summary>
    [RelayCommand]
    private void ShowClaude()
    {
        if (!Docking.ShowPage(AgentChatDockable.TabId))
            Docking.OpenPage(new AgentChatDockable(Claude));
        Claude.RequestFocus();
    }

    /// <summary>View > New Claude Chat (Ctrl+Alt+N): a fresh conversation, in the Claude tab.</summary>
    [RelayCommand]
    private void NewClaudeChat()
    {
        ShowClaude();
        Claude.NewChatCommand.Execute(null);
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
    private Task<bool> CloseSolution() => CloseWorkspaceAsync();

    /// <summary>Open a solution or folder from the start window, the recent list, a clone or a new project.</summary>
    public async void Receive(OpenWorkspaceMessage message) => await OpenWorkspaceAsync(message.Path);

    /// <summary>
    /// Ask whether <paramref name="path"/> is trusted when it isn't yet, close what's open (asking about unsaved
    /// changes), then open it. False if the user cancels either question.
    /// </summary>
    public async Task<bool> OpenWorkspaceAsync(string path)
    {
        if (!await ConfirmTrustAsync(path))
            return false;
        if (Workspace.IsOpen && !await CloseWorkspaceAsync())
            return false;
        return await Workspace.OpenAsync(path);
    }

    /// <summary>
    /// Rider's "Trust and Open Project": opening a solution runs its code (MSBuild evaluates its projects, the language
    /// server loads its analyzers and generators, previews load its assemblies), so a solution or folder from outside the
    /// trusted folders (WorkspaceTrust) is asked about first. Cancel, the default, opens nothing. True if trusted.
    /// </summary>
    private async Task<bool> ConfirmTrustAsync(string path)
    {
        if (_trust.IsTrusted(path))
            return true;

        var folder = WorkspaceTrust.FolderOf(path);
        // Its parent too (e.g. ~/RiderProjects, so the projects kept there are asked about once), but not the home
        // folder or a drive's root, which would trust every download.
        var parent = System.IO.Path.GetDirectoryName(folder);
        if (parent is not null && (System.IO.Path.GetPathRoot(parent) == parent
                                   || WorkspaceTrust.FolderOf(parent) == WorkspaceTrust.FolderOf(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))
            parent = null;
        var trustParent = parent is null ? null : $"Trust '{(System.IO.Path.GetFileName(parent) is { Length: > 0 } name ? name : parent)}'";
        var choice = await _dialogs.WarnAsync("Trust and Open",
            $"Opening '{System.IO.Path.GetFileName(path)}' runs code from it: building and loading its projects can run " +
            "any command, and its analyzers, source generators and previews run as you.\n\n" +
            "Only open it if you trust where it came from.",
            trustParent is null ? ["Trust and Open"] : [trustParent, "Trust and Open"],
            [folder]);
        if (choice is null)
            return false;
        _trust.Trust(choice == trustParent ? parent! : folder);
        return true;
    }

    /// <summary>
    /// The main window is closing (File > Exit or its X): offer to stop debugging, ask about unsaved changes. False if
    /// the user cancelled or a save failed, and the window stays open.
    /// </summary>
    public Task<bool> ConfirmExitAsync() => ConfirmLeaveAsync();

    /// <summary>Ask about debugging and unsaved changes, then close every document and the workspace. False if the user cancelled or a save failed.</summary>
    private async Task<bool> CloseWorkspaceAsync()
    {
        if (!await ConfirmLeaveAsync())
            return false;

        Session.Close();   // remember what was open before closing it
        foreach (var document in _documents.Documents.ToArray())
            _documents.Close(document, force: true);
        Docking.CloseAllPages();

        _documents.Active = null;
        Workspace.Close();
        return true;
    }

    /// <summary>
    /// Before the workspace or the app closes, as Visual Studio does: "Do you want to stop debugging?" while a debug
    /// session is going, then one save prompt listing the documents with unsaved changes. Nothing is stopped or saved
    /// until every question has been answered, so a Cancel leaves everything as it was. The debug session is ended cleanly
    /// (the debugger is told to disconnect and end the program) before returning true.
    /// </summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (Run.IsDebugging
            && !await _dialogs.ConfirmAsync("Stop Debugging", "Do you want to stop debugging?", "Stop Debugging"))
            return false;

        var dirtyDocuments = _documents.Documents.Where(document => document.IsDirty).ToArray();
        if (dirtyDocuments.Length > 0)
        {
            var choice = await _dialogs.AskSaveAsync(dirtyDocuments);   // one question for all, as Visual Studio asks
            if (choice == SaveChoice.Cancel) return false;
            if (choice == SaveChoice.Save)
                foreach (var document in dirtyDocuments)
                    if (!await _saver.SaveAsync(document))
                        return false;   // a save failed: keep everything open
        }

        await Run.StopDebuggingAsync();
        return true;
    }

    // ---------------------------------------------------------------- Window and Help menus

    /// <summary>The Window menu's list of open documents (Visual Studio lists them at the bottom of the menu).</summary>
    public System.Collections.ObjectModel.ObservableCollection<OpenDocument> OpenDocuments => _documents.Documents;

    [RelayCommand]
    private void ActivateDocument(OpenDocument? document)
    {
        if (document is not null)
            _documents.BringToFront(document);
    }

    /// <summary>Help > About AXamlIDE.</summary>
    [RelayCommand]
    private Task ShowAbout()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var version = (System.Reflection.CustomAttributeExtensions
                           .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion
                       ?? assembly.GetName().Version?.ToString() ?? "").Split('+')[0];
        return _dialogs.ShowMessageAsync("About AXamlIDE",
            $"AXamlIDE {version}\n\nAn IDE for Avalonia and .NET, built with Avalonia.\n\n"
            + $".NET {Environment.Version}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n\n"
            + $"Settings and logs: {System.IO.Path.GetDirectoryName(Services.Diagnostics.CrashLog.Folder)}");
    }

    /// <summary>Help > View Crash Log: the log of unhandled exceptions (Program.cs, App.axaml.cs), in a tab.</summary>
    [RelayCommand]
    private void ViewCrashLog()
    {
        var path = Services.Diagnostics.CrashLog.FilePath;
        if (!System.IO.File.Exists(path))
        {
            ShowStatusMessage("No crashes have been logged.");
            return;
        }
        try
        {
            _documents.Active = _documents.Open(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowStatusMessage($"Couldn't open the crash log: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RestartLanguageServices() => _languages.Restart();

    /// <summary>Window > Reset Window Layout, as in Visual Studio.</summary>
    [RelayCommand]
    private void ResetWindowLayout() => Docking.ResetLayout();

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
