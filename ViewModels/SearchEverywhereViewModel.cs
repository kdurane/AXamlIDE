using System;
using System.Collections.Generic;
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
/// Search Everywhere, as Rider's (double Shift): a window that finds types, files, symbols, IDE actions and options
/// as you type, with tabs to narrow it down, and Text sends the words to the Search window. Choosing a row closes it
/// and clears the box. The window is Views/SearchEverywhereWindow; the top bar's button opens it.
/// </summary>
public sealed partial class SearchEverywhereViewModel : ObservableObject
{
    // Rows per section on the All tab; a tab of its own shows up to MaxRows.
    private const int AllTabRows = 6;
    private const int MaxRows = 100;

    // The list changes once per pause in typing, not on every key, and waits this long for the language server's
    // symbols so they come in with the rest rather than pushing the list down a moment later.
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan SymbolWait = TimeSpan.FromMilliseconds(400);

    private readonly IDocumentHost _documents;
    private readonly DocumentManager _openDocuments;
    private readonly WorkspaceModel _workspace;
    private readonly LanguageSessionService _languages;
    private readonly NavigationService _navigator;
    private readonly SettingsViewModel _settings;

    private IReadOnlyList<string> _files = [];
    private IReadOnlyList<LspLocation> _symbols = [];
    private string _symbolsQuery = "";
    private CancellationTokenSource? _update;

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

    /// <summary>An option row: open Settings at its page with it focused (set by MainWindowViewModel).</summary>
    public Action<string, string> OpenSettingsAt { get; set; } = (_, _) => { };

    /// <summary>The window is to show, with the keyboard in its box (Open from the button, a key or a menu).</summary>
    public event Action? ShowRequested;

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial SearchEverywhereTab SelectedTab { get; set; }
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial int SelectedIndex { get; set; } = -1;

    /// <summary>The rows; replaced as a whole when they change, so the list redraws once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    public partial IReadOnlyList<SearchEverywhereItem> Results { get; set; } = [];

    /// <summary>Nothing matched: the window says so rather than showing an empty list.</summary>
    public bool HasNoResults => Results.Count == 0;

    // ---------------------------------------------------------------- opening and closing

    /// <summary>Navigate > Search Everywhere (double Shift) or Find Action (the Actions tab): show the window.</summary>
    [RelayCommand]
    private void Open(SearchEverywhereTab tab)
    {
        SelectedTab = tab;
        if (!IsOpen)
        {
            IsOpen = true;
            _ = LoadFilesAsync();
            Rebuild();
        }
        ShowRequested?.Invoke();
    }

    /// <summary>The window has closed (Escape, a row chosen, or a click back in the IDE): clear it for next time.</summary>
    public void Close()
    {
        _update?.Cancel();
        IsOpen = false;
        Query = "";
        _symbols = [];
        _symbolsQuery = "";
        Results = [];
        SelectedIndex = -1;
    }

    partial void OnQueryChanged(string value)
    {
        if (IsOpen)
            UpdateSoon();
    }

    partial void OnSelectedTabChanged(SearchEverywhereTab value)
    {
        if (IsOpen)
            Rebuild();
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
    /// Enter, or a click: the row's action, for the window to run once it has closed and the main window has the focus
    /// back. Null when nothing is selected.
    /// </summary>
    public Action? Choose(SearchEverywhereItem? item = null)
    {
        item ??= SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;
        if (item is null)
            return null;
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
            Rebuild();
    }

    /// <summary>
    /// Typing: once it pauses, ask the language server for the symbols (waiting a little for them), then show the
    /// lot at once. A slow server's symbols are added when they come.
    /// </summary>
    private void UpdateSoon()
    {
        _update?.Cancel();
        var cancellation = _update = new CancellationTokenSource();
        _ = UpdateAsync(Query.Trim(), cancellation.Token);
    }

    private async Task UpdateAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TypingPause, ct);
            if (query.Length == 0)
            {
                Rebuild();
                return;
            }
            var symbols = SearchSymbolsAsync(query, ct);
            if (await Task.WhenAny(symbols, Task.Delay(SymbolWait, ct)) != symbols)
            {
                Rebuild();   // the rest now; the symbols when they come
                await symbols;
            }
            ct.ThrowIfCancellationRequested();
            Rebuild();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SearchSymbolsAsync(string query, CancellationToken ct)
    {
        try
        {
            if (await _languages.ClientForLanguageAsync(LanguageIds.CSharp) is not { SupportsWorkspaceSymbols: true } client)
                return;
            var symbols = await client.WorkspaceSymbolsAsync(query, ct);
            if (ct.IsCancellationRequested)
                return;
            _symbols = symbols;
            _symbolsQuery = query;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Server busy or gone: files and actions still show.
        }
    }

    /// <summary>The list for the query and tab, keeping the selected row selected if it's still there.</summary>
    private void Rebuild()
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

        var results = new List<SearchEverywhereItem>();
        foreach (var section in sections)
        {
            var first = true;
            foreach (var item in section)
            {
                item.ShowsSection = first && tab == SearchEverywhereTab.All;
                first = false;
                results.Add(item);
            }
        }

        // A row the user moved down to stays selected; otherwise the best match (the first row) is.
        var selectedKey = SelectedIndex > 0 && SelectedIndex < Results.Count ? Results[SelectedIndex].Key : null;
        Results = results;
        var kept = selectedKey is null ? -1 : results.FindIndex(r => r.Key == selectedKey);
        SelectedIndex = kept >= 0 ? kept : results.Count > 0 ? 0 : -1;
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
            Icon = "Icon.Cog",
            Section = "Actions",
            Score = Match(option.Name, option.Page, query),
            GetOption = option.Get,
            SetOption = option.Set,
            Run = () => OpenSettingsAt(option.Page, option.CheckBox ?? option.Name)
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

    /// <param name="CheckBox">The check box's text in the Settings window, when the name here says more.</param>
    private sealed record Option(string Name, string Page, Func<bool> Get, Action<bool> Set, string? CheckBox = null);

    /// <summary>The Settings window's switches, by the names it shows them under (its page is the tab's header).</summary>
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
            new("Format documents on save", "Text Editor", () => s.FormatOnSave, v => s.FormatOnSave = v, "Format documents on save when supported"),
            new("Show the completion list while typing", "IntelliSense", () => s.CompletionWhileTyping, v => s.CompletionWhileTyping = v),
            new("Show items from namespaces that aren't imported", "IntelliSense", () => s.CompletionFromUnimportedNamespaces, v => s.CompletionFromUnimportedNamespaces = v),
            new("Show parameter info while typing", "IntelliSense", () => s.ParameterInfoWhileTyping, v => s.ParameterInfoWhileTyping = v),
            new("Show snippets in the completion list", "IntelliSense", () => s.SnippetsInCompletion, v => s.SnippetsInCompletion = v),
            new("Expand a snippet with Tab after its shortcut", "IntelliSense", () => s.TabExpandsSnippets, v => s.TabExpandsSnippets = v),
            new("Show Quick Info on hover", "IntelliSense", () => s.QuickInfoOnHover, v => s.QuickInfoOnHover = v),
            new("Analyse the entire solution", "IntelliSense", () => s.SolutionWideAnalysis, v => s.SolutionWideAnalysis = v),
            new("Inlay hints: parameter names", "Inline Hints", () => s.InlayParameterHints, v => s.InlayParameterHints = v, "Parameter names"),
            new("Inlay hints: types", "Inline Hints", () => s.InlayTypeHints, v => s.InlayTypeHints = v, "Types"),
            new("CodeLens: show references", "Inline Hints", () => s.CodeLens, v => s.CodeLens = v, "Show references"),
            new("Show inline values while debugging", "Debugging", () => s.InlineDebugValues, v => s.InlineDebugValues = v, "Show inline values"),
            new("Break when any exception is thrown", "Debugging", () => s.BreakOnAllExceptions, v => s.BreakOnAllExceptions = v),
            new("Remember breakpoints between sessions", "Debugging", () => s.RememberBreakpoints, v => s.RememberBreakpoints = v),
            new("Track active item in Solution Explorer", "Environment", () => s.TrackActiveItem, v => s.TrackActiveItem = v),
            new("Show Solution Explorer on the right", "Environment", () => s.SolutionExplorerOnRight, v => s.SolutionExplorerOnRight = v),
            new("Let Claude use the IDE's tools", "Claude", () => s.ClaudeIdeTools, v => s.ClaudeIdeTools = v),
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
