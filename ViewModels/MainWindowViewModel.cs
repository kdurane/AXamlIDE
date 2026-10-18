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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DocumentCommands = AXamlIDE.Commands.DocumentCommands;

namespace AXamlIDE.Viewmodels;

/// <summary>
/// The main window's view model: the one main UI (menus, activity bar, status bar) and the panels it
/// places. Each panel has its own view model (Explorer, DocumentWell, the tool panels); this class
/// only holds them and owns what belongs to the window as a whole: which tool panel is in front,
/// whether the explorer is shown, the status bar text, and workspace-wide commands.
///
/// Handles <see cref="StatusMessage"/>, <see cref="ShowToolTabMessage"/> and
/// <see cref="EditorStateChangedMessage"/> from any area.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject,
    IRecipient<StatusMessage>, IRecipient<ShowToolTabMessage>, IRecipient<EditorStateChangedMessage>
{
    private readonly DocumentManager _documents;
    private readonly DocumentSaveService _saver;
    private readonly LanguageSessionService _languages;
    private readonly SettingsViewModel _settings;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;

    public MainWindowViewModel(
        DocumentManager documents,
        DocumentSaveService saver,
        DocumentCommands documentCommands,
        LanguageSessionService languages,
        WorkspaceViewModel workspace,
        ExplorerViewModel explorer,
        DocumentWellViewModel documentWell,
        ProblemsViewModel problems,
        RunViewModel run,
        EditorCommands editorCommands,
        SettingsViewModel settings,
        EditorSettingsLoadResult settingsLoad,
        IDialogService dialogs,
        IMessenger messenger)
    {
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
        DocumentWell = documentWell;
        Problems = problems;
        Run = run;

        _documents.ActiveChanged += _ =>
        {
            OnPropertyChanged(nameof(IsEditorVisible));
            OnPropertyChanged(nameof(IsWelcomeVisible));
            UpdateEditorStatus();
        };
        messenger.RegisterAll(this);

        if (settingsLoad.Warnings.Count > 0)
            ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));
    }

    // ---------------------------------------------------------------- panels

    public ExplorerViewModel Explorer { get; }
    public DocumentWellViewModel DocumentWell { get; }
    /// <summary>Id of the tool panel in front (a <see cref="ToolTab"/> id); the window shows the matching panel.</summary>
    [ObservableProperty] public partial string SelectedToolPanelId { get; set; } = ToolTab.Output;

    // Areas the menus and KeyMap bind to.
    public WorkspaceViewModel Workspace { get; }
    public ProblemsViewModel Problems { get; }
    public RunViewModel Run { get; }

    /// <summary>Save / close / format, acting on the selected tab wherever it is.</summary>
    public DocumentCommands DocumentCommands { get; }

    /// <summary>Editor actions (go to definition, rename...) for menus and key bindings.</summary>
    public EditorCommands EditorCommands { get; }

    // ---------------------------------------------------------------- window state

    public bool IsEditorVisible => _documents.Active is not null;
    public bool IsWelcomeVisible => _documents.Active is null;

    [ObservableProperty] public partial bool IsExplorerVisible { get; set; } = true;
    [ObservableProperty] public partial string StatusText { get; set; } = "language servers: idle";
    [ObservableProperty] public partial string CaretText { get; set; } = "";
    [ObservableProperty] public partial string DiagnosticsText { get; set; } = "";

    public void ShowStatusMessage(string message) => StatusText = message;

    public void Receive(StatusMessage message) => ShowStatusMessage(message.Text);

    public void Receive(ShowToolTabMessage message)
        => SelectedToolPanelId = message.PanelId;

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
        SelectedToolPanelId = ToolTab.Search;
        _messenger.Send(new FocusFindInFilesMessage());
    }

    [RelayCommand]
    private void ToggleExplorer() => IsExplorerVisible = !IsExplorerVisible;

    /// <summary>Ask about unsaved changes, then close every document and the workspace.</summary>
    [RelayCommand]
    private async Task CloseSolution()
    {
        var dirtyDocuments = _documents.Documents.Where(document => document.IsDirty).ToArray();
        var decisions = new List<(OpenDocument Document, SaveChoice Choice)>(dirtyDocuments.Length);
        foreach (var document in dirtyDocuments)
        {
            var choice = await _dialogs.AskSaveAsync(document);
            if (choice == SaveChoice.Cancel) return;
            decisions.Add((document, choice));
        }

        foreach (var (document, choice) in decisions)
            if (choice == SaveChoice.Save && !await _saver.SaveAsync(document))
                return;   // a save failed: keep everything open

        if (Run.IsDebugging) Run.StopRunningProject();

        foreach (var document in _documents.Documents.ToArray())
            _documents.Close(document, force: true);

        _documents.Active = null;
        Workspace.Close();
    }

    [RelayCommand]
    private void RestartLanguageServices() => _languages.Restart();

    [RelayCommand]
    private void ToggleSolutionExplorerSide()
        => _settings.SolutionExplorerOnRight = !_settings.SolutionExplorerOnRight;

    [RelayCommand]
    private Task OpenSettings() => _dialogs.ShowSettingsAsync();
}
