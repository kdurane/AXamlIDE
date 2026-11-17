using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Search;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Search Everywhere, as Rider's (double Shift): the top bar's box finds types, files, symbols, IDE actions and
/// options as you type, with tabs to narrow it down, and Text sends the words to the Search window. Choosing a row
/// clears the box. The box and its drop-down are Views/SearchEverywhereView.
/// </summary>
public sealed partial class SearchEverywhereViewModel : ObservableObject
{
    // Rows per section on the All tab; a tab of its own shows up to MaxRows.
    private const int AllTabRows = 6;
    private const int MaxRows = 100;

    private readonly IDocumentHost _documents;
    private readonly DocumentManager _openDocuments;
    private readonly WorkspaceModel _workspace;
    private readonly LanguageSessionService _languages;
    private readonly NavigationService _navigator;
    private readonly SettingsViewModel _settings;

    private IReadOnlyList<string> _files = [];
    private IReadOnlyList<LspLocation> _symbols = [];
    private string _symbolsQuery = "";
    private CancellationTokenSource? _symbolSearch;

    public SearchEverywhereViewModel(IDocumentHost documents, DocumentManager openDocuments, WorkspaceModel workspace,
        LanguageSessionService languages, NavigationService navigator, SettingsViewModel settings)
    {
        _documents = documents;
        _openDocuments = openDocuments;
        _workspace = workspace;
        _languages = languages;
        _navigator = navigator;
        _settings = settings;
    }

    public SearchEverywhereTab[] Tabs { get; } = Enum.GetValues<SearchEverywhereTab>();

    /// <summary>The IDE's commands, with their keys (set by MainWindow from Shell.KeyMap, which knows them).</summary>
    public Func<IEnumerable<IdeAction>> Actions { get; set; } = () => [];

    /// <summary>Text: search the workspace's files for the words, in the Search window (set by MainWindowViewModel).</summary>
    public Action<string> FindInFiles { get; set; } = _ => { };

    /// <summary>The view puts the keyboard focus in the box (Open from a key or menu).</summary>
    public event Action? FocusRequested;

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial SearchEverywhereTab SelectedTab { get; set; }
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial int SelectedIndex { get; set; } = -1;

    public ObservableCollection<SearchEverywhereItem> Results { get; } = [];

    /// <summary>Nothing matched: the drop-down says so rather than showing an empty box.</summary>
    public bool HasNoResults => Results.Count == 0;

    public string PlaceholderText => "Search Everywhere (Double Shift)";

    // ---------------------------------------------------------------- opening and closing

    /// <summary>Navigate > Search Everywhere (double Shift) or Find Action (the Actions tab): open with the box focused.</summary>
    [RelayCommand]
    private void Open(SearchEverywhereTab tab)
    {
        SelectedTab = tab;
        Show();
        FocusRequested?.Invoke();
    }

    /// <summary>The drop-down opens (the box got the focus, or something was typed): list the files again, refresh.</summary>
    public void Show()
    {
        if (!IsOpen)
        {
            IsOpen = true;
            _ = LoadFilesAsync();
        }
        Refresh();
    }

    /// <summary>Escape, a click elsewhere, or a row chosen: close and clear the box, as VS Code and Rider do.</summary>
    public void Close()
    {
        _symbolSearch?.Cancel();
        IsOpen = false;
        Query = "";
        _symbols = [];
        _symbolsQuery = "";
        Results.Clear();
    }

    // Closed by a click outside it (the popup's light dismiss): clear up as Escape does.
    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
            Close();
    }

    partial void OnQueryChanged(string value)
    {
        if (value.Length > 0 && !IsOpen)
            Show();
        else if (IsOpen)
            Refresh();
        SearchSymbolsSoon();
    }

    partial void OnSelectedTabChanged(SearchEverywhereTab value)
    {
        if (IsOpen)
            Refresh();
    }

    /// <summary>Tab / Shift+Tab: the next or previous tab, as in Rider.</summary>
    public void NextTab(int step)
        => SelectedTab = Tabs[(Array.IndexOf(Tabs, SelectedTab) + step + Tabs.Length) % Tabs.Length];

    /// <summary>Up / Down in the box move the selection, wrapping round.</summary>
    public void MoveSelection(int step)
    {
        if (Results.Count > 0)
            SelectedIndex = (SelectedIndex + step + Results.Count) % Results.Count;
    }

    /// <summary>
    /// Enter, or a click: what the row does. Returns the row's action for the view to run once the popup is gone and
    /// the focus is back where it was (null for an option, which runs here and keeps the list).
    /// </summary>
    public Action? Choose(SearchEverywhereItem? item = null)
    {
        item ??= SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;
        if (item is null)
            return null;
        if (item.KeepsOpen)
        {
            var index = SelectedIndex;
            item.Run();
            Refresh();
            SelectedIndex = Math.Min(index, Results.Count - 1);
            return null;
        }
        Close();
        return item.Run;
    }

    // ---------------------------------------------------------------- the results

    private async Task LoadFilesAsync()
    {
        try
        {
            _files = await _documents.GetWorkspaceFilesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _files = [];
        }
        if (IsOpen)
            Refresh();
    }

    /// <summary>Rebuild the list for the query and tab. Symbols come from the language server, a moment later.</summary>
    private void Refresh()
    {
        var query = Query.Trim();
        var tab = SelectedTab;
        var rows = tab == SearchEverywhereTab.All ? AllTabRows : MaxRows;
        var sections = new List<IEnumerable<SearchEverywhereItem>>();

        if (tab is SearchEverywhereTab.All or SearchEverywhereTab.Types)
            sections.Add(SymbolRows(query, types: true).Take(rows));
        if (tab is SearchEverywhereTab.All or SearchEverywhereTab.Files)
            sections.Add(FileRows(query).Take(rows));
        if (tab is SearchEverywhereTab.All or SearchEverywhereTab.Symbols)
            sections.Add(SymbolRows(query, types: false).Take(rows));
        if (tab is SearchEverywhereTab.All or SearchEverywhereTab.Actions)
            sections.Add(ActionRows(query).Take(tab == SearchEverywhereTab.All ? AllTabRows : int.MaxValue));
        if (tab is SearchEverywhereTab.All or SearchEverywhereTab.Text && query.Length > 0)
            sections.Add([TextRow(query)]);

        Results.Clear();
        foreach (var section in sections)
        {
            var first = true;
            foreach (var item in section)
            {
                item.ShowsSection = first && tab == SearchEverywhereTab.All;
                first = false;
                Results.Add(item);
            }
        }
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>Files by name; with nothing typed, the open tabs (Rider shows recent files).</summary>
    private IEnumerable<SearchEverywhereItem> FileRows(string query)
    {
        if (query.Length == 0)
            return _openDocuments.Documents.Where(d => !d.IsReadOnly).Select(d => FileRow(d.Path, 0));
        return _files
            .Select(path => (path, score: FuzzyMatch.Score(Path.GetFileName(path), query)))
            .Where(match => match.score is not null)
            .OrderByDescending(match => match.score)
            .ThenBy(match => match.path.Length)
            .Select(match => FileRow(match.path, match.score!.Value));
    }

    private SearchEverywhereItem FileRow(string path, int score) => new()
    {
        Title = Path.GetFileName(path),
        Detail = RelativeFolder(path),
        Icon = ProjectTreeNode.FileIcon(path),
        Section = "Files",
        Score = score,
        Run = () => _documents.OpenAndActivate(path)
    };

    private string RelativeFolder(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        return _workspace.RootDirectory is { } root && folder.StartsWith(root, StringComparison.Ordinal)
            ? folder[root.Length..].TrimStart(Path.DirectorySeparatorChar)
            : folder;
    }

    // LSP SymbolKind: class, enum, interface, struct.
    private static bool IsType(int kind) => kind is 5 or 10 or 11 or 23;

    private IEnumerable<SearchEverywhereItem> SymbolRows(string query, bool types)
    {
        if (query.Length == 0 || !string.Equals(_symbolsQuery, query, StringComparison.Ordinal))
            return [];
        var symbols = _symbols.Where(symbol => IsType(symbol.Kind) == types).ToList();
        // A partial class is in a source-generated file too (MainWindow.g.cs): show it once, at the user's file.
        var written = symbols.Where(s => !LspHelpers.IsSourceGenerated(s.Uri)).Select(s => s.Name).ToHashSet();
        return symbols
            .Where(symbol => !LspHelpers.IsSourceGenerated(symbol.Uri) || !written.Contains(symbol.Name))
            .Select(symbol => (symbol, score: FuzzyMatch.Score(symbol.Name ?? "", query) ?? 0))
            .OrderByDescending(match => match.score)
            .Select(match => new SearchEverywhereItem
            {
                Title = match.symbol.Name ?? "(unnamed)",
                Detail = SymbolDetail(match.symbol),
                Icon = types ? "Icon.CodeBraces" : "Icon.CSharp",
                Section = types ? "Types" : "Symbols",
                Score = match.score,
                Run = () => _ = GoToSymbolAsync(match.symbol)
            });
    }

    /// <summary>
    /// The type it's in, then its file. Roslyn's container name is wordy and has two shapes: "in Program (project
    /// App (net10.0))" for a member, "MainWindow.axaml.cs - App (net10.0)" (file and project) for a type.
    /// </summary>
    private static string SymbolDetail(LspLocation symbol)
    {
        var file = LspHelpers.IsSourceGenerated(symbol.Uri)
            ? LspHelpers.SourceGeneratedName(symbol.Uri)
            : Path.GetFileName(FileUris.ToPath(symbol.Uri) ?? "");
        var container = symbol.ContainerName ?? "";
        if (container.StartsWith("in ", StringComparison.Ordinal))
        {
            container = container[3..];
            var project = container.IndexOf(" (project ", StringComparison.Ordinal);
            if (project >= 0)
                container = container[..project];
        }
        else
        {
            container = "";   // a type: the file says it all
        }
        return container.Length == 0 ? file : $"{container}  ·  {file}";
    }

    /// <summary>Ask the C# server for the symbols, once typing pauses; the list is rebuilt when they arrive.</summary>
    private void SearchSymbolsSoon()
    {
        _symbolSearch?.Cancel();
        var query = Query.Trim();
        if (query.Length == 0 || !IsOpen)
            return;
        var cancellation = _symbolSearch = new CancellationTokenSource();
        _ = SearchSymbolsAsync(query, cancellation.Token);
    }

    private async Task SearchSymbolsAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
            if (await _languages.ClientForLanguageAsync(LanguageIds.CSharp) is not { SupportsWorkspaceSymbols: true } client)
                return;
            var symbols = await client.WorkspaceSymbolsAsync(query, ct);
            if (ct.IsCancellationRequested || !IsOpen)
                return;
            _symbols = symbols;
            _symbolsQuery = query;
            var selected = SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;
            Refresh();
            // Keep the row the user had moved to, if it's still there (a file below the new types).
            if (selected is not null && SelectedIndex == 0 && Results.FirstOrDefault(r => r.Title == selected.Title && r.Section == selected.Section) is { } same)
                SelectedIndex = Results.IndexOf(same);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Server busy or gone: files and actions still show.
        }
    }

    private async Task GoToSymbolAsync(LspLocation symbol)
    {
        if (!LspHelpers.IsSourceGenerated(symbol.Uri))
        {
            _navigator.ToLocation(symbol);
            return;
        }
        if (await _languages.ClientForLanguageAsync(LanguageIds.CSharp) is { } client
            && await client.SourceGeneratedTextAsync(symbol.Uri) is { } text)
            _navigator.ToGeneratedLocation(symbol, text);
    }

    /// <summary>IDE commands and on/off options; with nothing typed, all of them (Rider's Find Action lists them too).</summary>
    private IEnumerable<SearchEverywhereItem> ActionRows(string query)
    {
        var commands = Actions().Select(action => new SearchEverywhereItem
        {
            Title = action.Info.Name,
            Detail = action.Info.Category,
            Shortcut = action.Shortcut,
            Icon = "Icon.Wrench",
            Section = "Actions",
            Score = Match(action.Info.Name, action.Info.Description, query),
            Run = () =>
            {
                if (action.Command.CanExecute(action.Parameter))
                    action.Command.Execute(action.Parameter);
            }
        });
        var options = Options().Select(option => new SearchEverywhereItem
        {
            Title = option.Name,
            Detail = $"Option · {option.Page}",
            Shortcut = option.Get() ? "On" : "Off",
            Icon = "Icon.Cog",
            Section = "Actions",
            Score = Match(option.Name, option.Page, query),
            KeepsOpen = true,
            Run = () => option.Set(!option.Get())
        });
        return commands.Concat(options)
            .Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Score);
    }

    /// <summary>By name first; a word of the description counts too, a lot less ("comment" finds Toggle Comment).</summary>
    private static int Match(string name, string description, string query)
    {
        if (query.Length == 0)
            return 0;
        if (FuzzyMatch.Score(name, query) is { } score)
            return score;
        return description.Contains(query, StringComparison.OrdinalIgnoreCase) ? 50 : -1;
    }

    private sealed record Option(string Name, string Page, Func<bool> Get, Action<bool> Set);

    /// <summary>The Settings window's switches, by the names it shows them under.</summary>
    private IEnumerable<Option> Options()
    {
        var s = _settings;
        return
        [
            new("Show line numbers", "Text Editor", () => s.ShowLineNumbers, v => s.ShowLineNumbers = v),
            new("Highlight current line", "Text Editor", () => s.HighlightCurrentLine, v => s.HighlightCurrentLine = v),
            new("Enable font ligatures", "Text Editor", () => s.FontLigatures, v => s.FontLigatures = v),
            new("Semantic highlighting", "Text Editor", () => s.SemanticHighlighting, v => s.SemanticHighlighting = v),
            new("Highlight other occurrences of the selection", "Text Editor", () => s.HighlightSelectionOccurrences, v => s.HighlightSelectionOccurrences = v),
            new("Colour swatches", "Text Editor", () => s.ColorSwatches, v => s.ColorSwatches = v),
            new("Automatic brace completion", "Text Editor", () => s.AutoCloseBraces, v => s.AutoCloseBraces = v),
            new("Close AXAML tags", "Text Editor", () => s.AutoCloseXamlTags, v => s.AutoCloseXamlTags = v),
            new("Format documents on save", "Text Editor", () => s.FormatOnSave, v => s.FormatOnSave = v),
            new("Show the completion list while typing", "IntelliSense", () => s.CompletionWhileTyping, v => s.CompletionWhileTyping = v),
            new("Show items from namespaces that aren't imported", "IntelliSense", () => s.CompletionFromUnimportedNamespaces, v => s.CompletionFromUnimportedNamespaces = v),
            new("Show parameter info while typing", "IntelliSense", () => s.ParameterInfoWhileTyping, v => s.ParameterInfoWhileTyping = v),
            new("Show snippets in the completion list", "IntelliSense", () => s.SnippetsInCompletion, v => s.SnippetsInCompletion = v),
            new("Expand a snippet with Tab after its shortcut", "IntelliSense", () => s.TabExpandsSnippets, v => s.TabExpandsSnippets = v),
            new("Show Quick Info on hover", "IntelliSense", () => s.QuickInfoOnHover, v => s.QuickInfoOnHover = v),
            new("Analyse the entire solution", "IntelliSense", () => s.SolutionWideAnalysis, v => s.SolutionWideAnalysis = v),
            new("Inlay hints: parameter names", "Inline Hints", () => s.InlayParameterHints, v => s.InlayParameterHints = v),
            new("Inlay hints: types", "Inline Hints", () => s.InlayTypeHints, v => s.InlayTypeHints = v),
            new("CodeLens: show references", "Inline Hints", () => s.CodeLens, v => s.CodeLens = v),
            new("Show inline values while debugging", "Debugging", () => s.InlineDebugValues, v => s.InlineDebugValues = v),
            new("Break when any exception is thrown", "Debugging", () => s.BreakOnAllExceptions, v => s.BreakOnAllExceptions = v),
            new("Track active item in Solution Explorer", "Environment", () => s.TrackActiveItem, v => s.TrackActiveItem = v),
            new("Show Solution Explorer on the right", "Environment", () => s.SolutionExplorerOnRight, v => s.SolutionExplorerOnRight = v),
        ];
    }

    private SearchEverywhereItem TextRow(string query) => new()
    {
        Title = $"Find \"{query}\" in Files",
        Detail = "Search window",
        Icon = "Icon.Search",
        Section = "Text",
        Run = () => FindInFiles(query)
    };
}
