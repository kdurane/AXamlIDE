using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Specialized;
using System.Xml;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AXamlIDE.Enums;
using AXamlIDE.Lsp;
using AXamlIDE.Models;
using AXamlIDE.Services;
using AXamlIDE.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels
{
    public sealed record DocumentOutlineItem(
        string DisplayName,
        int Kind,
        int StartOffset,
        int EndOffset,
        int NavigationOffset);

    public partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        private readonly DocumentManager _documents = new();
        private readonly LspManager _languageServers = new(LspServers.Defaults);
        private readonly WorkspaceSearchService _workspaceSearch = new();
        private readonly ProblemNavigationService _problemNavigation = new();
        private readonly WorkspaceModel _workspace;
        private readonly XamlLanguageService _xaml;
        private CancellationTokenSource? _outlineRequestCts;
        private int _outlineRequestSequence;
        private readonly IStorageProvider _storageProvider;

        public ObservableCollection<ProjectTreeNode> WorkspaceRoots => _workspace.WorkspaceRoots;
        public ObservableCollection<OpenDocument> Documents => _documents.Documents;
        public ObservableCollection<ProblemItem> Problems { get; } = [];
        public ObservableCollection<WorkspaceSearchResult> SearchResults { get; } = [];
        public ObservableCollection<DocumentOutlineItem> DocumentOutlineItems { get; } = [];

        [ObservableProperty] public partial int SelectedToolTabIndex { get; set; } = 1;
        [ObservableProperty] public partial object? SelectedDocTabItem { get; set; }
        [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
        [ObservableProperty] public partial string ReplaceText { get; set; } = string.Empty;
        [ObservableProperty] private partial bool IsSearching { get; set; }
        [ObservableProperty] public partial string SearchStatus { get; set; } = "Search the current workspace.";
        [ObservableProperty] public partial string LspStatus { get; set; } = "language servers: idle";
        [ObservableProperty] public partial OpenDocument? ActiveDocument { get; set; }
        public bool IsEditorVisible => ActiveDocument is not null;
        public bool IsWelcomeVisible => ActiveDocument is null;
        [ObservableProperty] public partial ProjectTreeNode? SelectedWorkspaceNode { get; set; }
        [ObservableProperty] public partial int ActiveCaretOffset { get; set; }
        [ObservableProperty] public partial DocumentOutlineItem? SelectedDocumentOutlineItem { get; set; }
        public bool IsDocumentOutlineVisible => DocumentOutlineItems.Count > 0;

        partial void OnActiveDocumentChanged(OpenDocument? value)
        {
            OnPropertyChanged(nameof(IsEditorVisible));
            OnPropertyChanged(nameof(IsWelcomeVisible));
        }

        public event Action<OpenDocument>? DocumentLanguageClientAttached;
        public event Action<OpenDocument>? DiagnosticsChanged;
        public event Action<ProblemItem>? NavigateToProblemRequested;
        public event Action<string, int>? DebugLocationChanged;
        public Func<JsonElement, Task<bool>>? WorkspaceEditRequested { get; set; }

        private readonly Window _owner;
        public SettingsViewModel Settings { get; }
        public RunViewModel Run { get; }

        public MainWindowViewModel(SettingsViewModel settings, IStorageProvider storageProvider,
        Window owner, WorkspaceModel workspace, XamlLanguageService xaml)
        {
            Settings = settings;
            _storageProvider = storageProvider;
            _owner = owner;
            _workspace = workspace;
            _xaml = xaml;
            Run = new RunViewModel(workspace, _documents.Documents, () => SelectedWorkspaceNode, ShowStatusMessage);
            Run.ToolTabRequested += index => SelectedToolTabIndex = index;

            _documents.Opened += OnDocumentOpened;
            _documents.Closing += OnDocumentClosing;
            _documents.Saved += OnDocumentSaved;
            DocumentOutlineItems.CollectionChanged += OnDocumentOutlineItemsChanged;
            _languageServers.StatusChanged += RefreshLspStatus;
            _languageServers.DiagnosticsPublished += OnDiagnosticsPublished;
            _languageServers.DiagnosticsRefreshNeeded += OnDiagnosticsRefreshNeeded;
            _languageServers.WorkspaceEditHandler = edit => WorkspaceEditRequested?.Invoke(edit)
                ?? Task.FromResult(false);
        }

        public async Task UpdateDocumentOutlineAsync(OpenDocument document)
        {
            var requestSequence = Interlocked.Increment(ref _outlineRequestSequence);
            _outlineRequestCts?.Cancel();
            _outlineRequestCts?.Dispose();
            _outlineRequestCts = null;

            if (!ReferenceEquals(ActiveDocument, document)
                || document.LanguageId != "csharp"
                || document.Client is not { } client)
            {
                ClearDocumentOutline();
                return;
            }

            var cancellation = new CancellationTokenSource();
            _outlineRequestCts = cancellation;
            try
            {
                var symbols = await client.DocumentSymbolsAsync(document.Uri, cancellation.Token);
                if (cancellation.IsCancellationRequested
                    || requestSequence != Volatile.Read(ref _outlineRequestSequence)
                    || !ReferenceEquals(ActiveDocument, document))
                    return;

                var items = symbols
                    .Where(symbol => IsDocumentOutlineKind(symbol.Kind))
                    .Select(symbol =>
                    {
                        var start = document.OffsetOf(symbol.Range.StartLine, symbol.Range.StartChar);
                        var end = document.OffsetOf(symbol.Range.EndLine, symbol.Range.EndChar);
                        var navigationOffset = document.OffsetOf(
                            symbol.SelectionRange.StartLine,
                            symbol.SelectionRange.StartChar);
                        var displayName = symbol.Name;
                        return new DocumentOutlineItem(
                            displayName,
                            symbol.Kind,
                            start,
                            Math.Max(start, end),
                            navigationOffset);
                    })
                    .ToArray();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (requestSequence != Volatile.Read(ref _outlineRequestSequence)
                        || !ReferenceEquals(ActiveDocument, document))
                        return;

                    DocumentOutlineItems.Clear();
                    foreach (var item in items)
                        DocumentOutlineItems.Add(item);
                    // SelectDocumentOutlineItem(ActiveCaretOffset);
                });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[outline] Could not retrieve symbols for '{document.Path}': {ex}");
                if (requestSequence == Volatile.Read(ref _outlineRequestSequence)
                    && ReferenceEquals(ActiveDocument, document))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ClearDocumentOutline();
                        ShowStatusMessage($"Couldn't update C# outline: {ex.Message}");
                    });
                }
            }
            finally
            {
                if (ReferenceEquals(_outlineRequestCts, cancellation))
                    _outlineRequestCts = null;
                cancellation.Dispose();
            }
        }

        private MsBuildHostClient Host => _workspace.Host;

        public void ClearDocumentOutline()
        {
            Interlocked.Increment(ref _outlineRequestSequence);
            _outlineRequestCts?.Cancel();
            DocumentOutlineItems.Clear();
            SelectedDocumentOutlineItem = null;
        }

        private static bool IsDocumentOutlineKind(int kind)
            => kind is 2 or 3 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 23 or 24 or 26;

        private void OnDocumentOutlineItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => OnPropertyChanged(nameof(IsDocumentOutlineVisible));


        private void OpenFolder(string path)
        {
            _workspace.SolutionProjects = [];
            _xaml.ClearPreviewTargets();
            SelectedWorkspaceNode = null;
            var root = ProjectTreeNode.CreateDirectory(path);
            root.Populate();
            WorkspaceRoots.Clear();
            WorkspaceRoots.Add(root);
            SetWorkspace(new LspWorkspace(path, SolutionPath: null));
        }

        private void OpenSolution(string path)
        {
            var root = SolutionLoader.LoadSolution(path, Host);
            _xaml.ClearPreviewTargets();
            SelectedWorkspaceNode = null;
            root.Populate();
            _workspace.SolutionProjects =
            [
                .. root.Children
                          .Where(node => node.Kind == NodeKind.Project)
                          .Select(node => node.FullPath)
            ];
            root.IsExpanded = true;
            WorkspaceRoots.Clear();
            WorkspaceRoots.Add(root);
            SetWorkspace(new LspWorkspace(Path.GetDirectoryName(path)!, SolutionPath: path));
        }

        public OpenDocument OpenDocument(string path) => _documents.Open(path);

        public OpenDocument OpenReadOnlyDocument(string path, string text, string languageId)
            => _documents.OpenReadOnly(path, text, languageId);

        public async Task SaveDocumentAsync(OpenDocument document)
        {
            if (document.IsReadOnly)
            {
                ShowStatusMessage("Decompiled source is read-only.");
                return;
            }

            if (Settings.FormatOnSave && document.Client?.SupportsDocumentFormatting == true)
                await FormatDocumentAsync(document, reportUnchanged: false);

            _documents.Save(document);
        }

        [RelayCommand]
        private async Task FormatActiveDocumentAsync()
        {
            if (ActiveDocument is not { } document)
            {
                ShowStatusMessage("Open a document with an active language server to format it.");
                return;
            }

            await FormatDocumentAsync(document, reportUnchanged: true);
        }

        private async Task<bool> FormatDocumentAsync(OpenDocument document, bool reportUnchanged)
        {
            if (document.Client is not { } client || document.Sync is not { } sync)
            {
                if (reportUnchanged)
                    ShowStatusMessage("Open a document with an active language server to format it.");
                return false;
            }

            if (!client.SupportsDocumentFormatting)
            {
                if (reportUnchanged)
                    ShowStatusMessage("The active language server does not support document formatting.");
                return false;
            }

            try
            {
                sync.Flush();
                var edits = await client.FormattingAsync(
                    document.Uri, tabSize: 4, insertSpaces: true);
                if (edits.Count == 0)
                {
                    if (reportUnchanged)
                        ShowStatusMessage("The language server returned no formatting changes.");
                    return true;
                }

                ApplyTextEdits(document, edits.Select(edit => (edit.Range, edit.NewText)));
                if (reportUnchanged)
                    ShowStatusMessage("Document formatted. Review and save the changes.");
                return true;
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Document formatting failed: {ex.Message}");
                return false;
            }
        }

        private static void ApplyTextEdits(
            OpenDocument document,
            IEnumerable<(LspRange Range, string NewText)> textEdits)
        {
            if (document.IsReadOnly)
                throw new InvalidOperationException($"Cannot edit read-only document '{document.Path}'.");

            var offsets = textEdits.Select(edit =>
            {
                var start = document.OffsetOf(edit.Range.StartLine, edit.Range.StartChar);
                var end = document.OffsetOf(edit.Range.EndLine, edit.Range.EndChar);
                if (end < start)
                    throw new InvalidOperationException(
                        $"The language server returned an invalid edit range for '{document.Path}'.");
                return (Start: start, End: end, edit.NewText);
            }).OrderByDescending(edit => edit.Start).ToList();

            for (var index = 1; index < offsets.Count; index++)
                if (offsets[index - 1].Start < offsets[index].End)
                    throw new InvalidOperationException(
                        $"The language server returned overlapping edits for '{document.Path}'.");

            foreach (var (start, end, newText) in offsets)
                document.Document.Replace(start, end - start, newText);
        }

        public void CloseDocument(OpenDocument document)
        {
            _documents.Close(document, force: true);
            RebuildProblems();
        }


        [RelayCommand]
        private Task SaveActiveDocumentAsync()
        {
            return ActiveDocument is { } document
                ? SaveDocumentAsync(document)
                : Task.CompletedTask;
        }


        [RelayCommand]
        private void RestartLanguageServices()
        {
            _languageServers.Restart();
            ReattachDocuments();
        }

        [RelayCommand]
        private void ToggleSolutionExplorerSide() => Settings.SolutionExplorerOnRight = !Settings.SolutionExplorerOnRight;

        [RelayCommand]
        private void NavigateDiagnostic(bool forward)
        {
            var location = _problemNavigation.FindNext(
                Problems.Select(problemItem => (problemItem.Document, problemItem.Offset)),
                Documents, ActiveDocument, ActiveCaretOffset, forward);
            if (location is not { } selected)
            {
                ShowStatusMessage("There are no diagnostics to navigate to.");
                return;
            }

            SelectedToolTabIndex = 0;
            if (Problems.FirstOrDefault(problemItem =>
                    ReferenceEquals(problemItem.Document, selected.Document)
                    && problemItem.Offset == selected.Offset) is { } problem)
                NavigateToProblemRequested?.Invoke(problem);
        }

        public void ShowStatusMessage(string message) => LspStatus = message;

        public async Task<IReadOnlyList<string>> GetWorkspaceFilesAsync()
        {
            if (WorkspaceRoots.FirstOrDefault() is not { } workspaceRoot)
                return [];

            var rootPath = workspaceRoot.Kind == NodeKind.Solution
                ? Path.GetDirectoryName(workspaceRoot.FullPath)!
                : workspaceRoot.FullPath;
            return await _workspaceSearch.ListWorkspaceFilesAsync(rootPath);
        }

        public async Task RefreshWorkspaceNodeAsync(ProjectTreeNode node)
        {
            node.Reset();
            await node.PopulateAsync();
        }

        public void RefreshWorkspace()
        {
            if (WorkspaceRoots.FirstOrDefault() is not { } root)
                return;

            if (root.Kind == NodeKind.Solution)
                OpenSolution(root.FullPath);
            else
                OpenFolder(root.FullPath);
        }

        [RelayCommand]
        private async Task SearchWorkspace()
        {
            if (IsSearching)
                return;

            SearchResults.Clear();
            var query = SearchQuery;
            if (string.IsNullOrWhiteSpace(query))
            {
                SearchStatus = "Enter text to search for.";
                return;
            }

            if (WorkspaceRoots.FirstOrDefault() is not { } workspaceRoot)
            {
                SearchStatus = "Open a folder or solution before searching.";
                return;
            }

            var rootPath = workspaceRoot.Kind == NodeKind.Solution
                ? Path.GetDirectoryName(workspaceRoot.FullPath)!
                : workspaceRoot.FullPath;
            var openDocuments = Documents.ToDictionary(
                document => Path.GetFullPath(document.Path),
                document => document.Document.Text,
                StringComparer.OrdinalIgnoreCase);

            IsSearching = true;
            SearchStatus = "Searching...";
            try
            {
                var summary = await _workspaceSearch.SearchAsync(rootPath, query, openDocuments);
                foreach (var result in summary.Results)
                    SearchResults.Add(result);

                var status = summary.ResultLimitReached
                    ? $"Showing the first {summary.Results.Count} results."
                    : $"{summary.Results.Count} result(s).";
                if (summary.SkippedLargeFiles > 0)
                    status += $" Skipped {summary.SkippedLargeFiles} file(s) larger than 2 MB.";
                if (summary.UnreadableFiles > 0)
                    status += $" Couldn't read {summary.UnreadableFiles} file(s) or folder(s).";
                SearchStatus = status;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                SearchStatus = $"Search failed: {ex.Message}";
            }
            finally
            {
                IsSearching = false;
            }
        }

        private async Task<WorkspaceReplacePlan?> PrepareWorkspaceReplaceAsync()
        {
            if (IsSearching)
            {
                SearchStatus = "Wait for the current search to finish before replacing.";
                return null;
            }

            var query = SearchQuery;
            if (string.IsNullOrWhiteSpace(query))
            {
                SearchStatus = "Enter text to search for before replacing.";
                return null;
            }

            if (WorkspaceRoots.FirstOrDefault() is not { } workspaceRoot)
            {
                SearchStatus = "Open a folder or solution before replacing.";
                return null;
            }

            var rootPath = workspaceRoot.Kind == NodeKind.Solution
                ? Path.GetDirectoryName(workspaceRoot.FullPath)!
                : workspaceRoot.FullPath;
            var openDocuments = Documents.ToDictionary(
                document => Path.GetFullPath(document.Path),
                document => document.Document.Text,
                StringComparer.OrdinalIgnoreCase);

            IsSearching = true;
            SearchStatus = "Preparing replace...";
            try
            {
                var plan = await _workspaceSearch.PrepareReplaceAsync(
                    rootPath, query, ReplaceText, openDocuments);
                SearchStatus = plan.LimitReached
                    ? "More than 5,000 matches found. Narrow the search before replacing."
                    : $"{plan.MatchCount} match(es) in {plan.Files.Count} file(s) ready to replace.";
                return plan;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                SearchStatus = $"Replace preparation failed: {ex.Message}";
                return null;
            }
            finally
            {
                IsSearching = false;
            }
        }

        private WorkspaceReplaceSummary ApplyWorkspaceReplacePlan(WorkspaceReplacePlan plan)
        {
            var openDocuments = Documents.ToDictionary(
                document => Path.GetFullPath(document.Path),
                document => document,
                StringComparer.OrdinalIgnoreCase);
            return _workspaceSearch.ApplyReplacePlan(plan, openDocuments);
        }

        private void SetWorkspace(LspWorkspace workspace)
        {
            _languageServers.SetWorkspace(workspace);
            ReattachDocuments();
        }

        private void ReattachDocuments()
        {
            foreach (var document in Documents)
            {
                document.Sync?.Dispose();
                document.Sync = null;
                document.Client = null;
                document.ClearDiagnostics();
                DiagnosticsChanged?.Invoke(document);
                if (_languageServers.Handles(document.LanguageId))
                    _ = AttachLspAsync(document);
            }

            RebuildProblems();
        }

        private void OnDocumentOpened(OpenDocument document)
        {
            Run.TrackDocument(document);
            if (!document.IsReadOnly && _languageServers.Handles(document.LanguageId))
                _ = AttachLspAsync(document);
        }


        private async Task AttachLspAsync(OpenDocument document)
        {
            var generation = _languageServers.Generation;
            var client = await _languageServers.GetClientAsync(document.LanguageId);
            if (client is null || generation != _languageServers.Generation
                               || !Documents.Contains(document) || document.Sync is not null)
                return;

            document.Client = client;
            client.DidOpen(document.Uri, document.LanguageId, 1, document.Document.Text);
            document.Sync = new DocumentSync(document.Document, client, document.Uri);
            DocumentLanguageClientAttached?.Invoke(document);
            document.Sync.Flushed += () =>
            {
                if (client.SupportsPullDiagnostics)
                    _ = PullDiagnosticsAsync(document);
            };

            if (client.SupportsPullDiagnostics)
                _ = PullDiagnosticsAsync(document);
        }

        private void OnDocumentClosing(OpenDocument document)
        {
            Run.UntrackDocument(document);
            if (document.Sync is not null)
                document.Client?.DidClose(document.Uri);
        }

        private void OnDocumentSaved(OpenDocument document)
        {
            if (document.Sync is not null)
                document.Client?.DidSave(document.Uri, document.Document.Text);
        }

        private void OnDiagnosticsPublished(string uri, IReadOnlyList<LspDiagnostic> diagnostics, int? version)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_documents.ByUri(uri) is { } document)
                    ApplyDiagnostics(document, diagnostics, version);
            });
        }

        private async Task PullDiagnosticsAsync(OpenDocument document)
        {
            if (document.Client is not { SupportsPullDiagnostics: true } client
                || document.Sync is not { } sync)
                return;

            document.PullCts?.Cancel();
            var cancellation = document.PullCts = new CancellationTokenSource();
            var version = sync.Version;

            try
            {
                var diagnostics = await client.PullDiagnosticsAsync(document.Uri, cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                    ApplyDiagnostics(document, diagnostics, version);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to pull diagnostics for '{document.Path}': {ex}");
            }
        }

        private void OnDiagnosticsRefreshNeeded(LspClient client)
        {
            foreach (var document in Documents.Where(document => ReferenceEquals(document.Client, client)))
                _ = PullDiagnosticsAsync(document);
        }

        private void ApplyDiagnostics(OpenDocument document, IReadOnlyList<LspDiagnostic> diagnostics, int? version)
        {
            if (!document.ApplyDiagnostics(diagnostics, version))
                return;

            RebuildProblems();
            DiagnosticsChanged?.Invoke(document);
        }

        private void RebuildProblems()
        {
            Problems.Clear();
            foreach (var document in Documents)
                foreach (var diagnostic in document.Diagnostics)
                {
                    var location = document.Document.GetLocation(diagnostic.StartOffset);
                    Problems.Add(new ProblemItem(
                        document,
                        diagnostic.Severity switch
                        {
                            1 => "Error",
                            2 => "Warning",
                            _ => "Info"
                        },
                        location.Line,
                        diagnostic.StartOffset,
                        diagnostic.Message));
                }
        }

        private void RefreshLspStatus()
        {
            var summary = _languageServers.StatusSummary;
            LspStatus = summary.Length > 0 ? summary : "language servers: idle";
        }


        [RelayCommand]
        private async Task OpenFolder()
        {
            var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open Project Folder",
                AllowMultiple = false
            });

            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

            OpenFolder(path);
        }

        [RelayCommand]
        private async Task CloseSolutionClick()
        {
            var dirtyDocuments = Documents.Where(document => document.IsDirty).ToArray();
            var decisions = new List<(OpenDocument Document, SaveChoice Choice)>(dirtyDocuments.Length);
            foreach (var document in dirtyDocuments)
            {
                var choice = await AskSaveAsync(document);
                if (choice == SaveChoice.Cancel) return;
                decisions.Add((document, choice));
            }

            foreach (var (document, choice) in decisions)
                if (choice == SaveChoice.Save)
                    await SaveDocumentAsync(document);

            // HideTip(resetOffset: true);
            CloseSolution();
        }

        private void CloseSolution()
        {
            if (Run.IsDebugging) Run.StopRunningProject();

            foreach (var document in Documents.ToArray())
                _documents.Close(document, force: true);

            WorkspaceRoots.Clear();
            SelectedWorkspaceNode = null;
            Problems.Clear();
            _workspace.SolutionProjects = [];
            _xaml.ClearPreviewTargets();
            ActiveDocument = null;
            _languageServers.ClearWorkspace();
        }

        private async Task<SaveChoice> AskSaveAsync(OpenDocument doc)
        {
            var result = SaveChoice.Cancel; // closing the window with the X = cancel

            Window? dialog = null;
            dialog = new Window
            {
                Title = "Unsaved changes",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 16,
                    Children =
                          {
                              new TextBlock { Text = $"Save changes to {doc.Title}?", TextWrapping = TextWrapping.Wrap },
                              new StackPanel
                              {
                                  Orientation = Orientation.Horizontal,
                                  Spacing = 8,
                                  HorizontalAlignment = HorizontalAlignment.Right,
                                  Children =
                                  {
                                      Make("Save", SaveChoice.Save),
                                      Make("Don't save", SaveChoice.Discard),
                                      Make("Cancel", SaveChoice.Cancel)
                                  }
                              }
                          }
                }
            };

            await dialog.ShowDialog(_owner);
            return result;

            Button Make(string text, SaveChoice choice)
            {
                var b = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
                b.Click += (_, _) =>
                {
                    result = choice;
                    dialog?.Close();
                };
                return b;
            }
        }

        [RelayCommand]
        private async Task OpenSolution()
        {
            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Solution",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Solution files") { Patterns = ["*.sln", "*.slnx"] }]
            });

            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

            try
            {
                OpenSolution(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                           or InvalidOperationException or ArgumentException)
            {
                ShowStatusMessage($"Couldn't open solution '{path}': {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task QuickOpenAsync()
        {
            try
            {
                var files = await GetWorkspaceFilesAsync();
                if (files.Count == 0)
                {
                    ShowStatusMessage("No workspace files are available to open.");
                    return;
                }

                var selectedPath = await ShowQuickOpenDialogAsync(files);
                if (selectedPath is null)
                    return;

                SelectedDocTabItem = OpenDocument(selectedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowStatusMessage($"Quick Open failed: {ex.Message}");
            }
        }



        private async Task<string?> ShowQuickOpenDialogAsync(IReadOnlyList<string> files)
        {
            string? selectedPath = null;
            var results = new ListBox { MaxHeight = 420, MinWidth = 560 };
            var query = new TextBox { PlaceholderText = "Type a file name..." };
            var window = new Window
            {
                Title = "Quick Open",
                Width = 620,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            query.TextChanged += (_, _) => UpdateResults();
            query.KeyDown += (_, args) =>
            {
                switch (args.Key)
                {
                    case Key.Enter:
                        args.Handled = true;
                        AcceptSelection();
                        break;
                    case Key.Down when results.ItemCount > 0:
                        results.Focus();
                        results.SelectedIndex = Math.Min(results.SelectedIndex + 1, results.ItemCount - 1);
                        args.Handled = true;
                        break;
                }
            };
            results.DoubleTapped += (_, _) => AcceptSelection();
            results.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                args.Handled = true;
                AcceptSelection();
            };

            UpdateResults();
            window.Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children = { query, results }
            };
            window.Opened += (_, _) => query.Focus();
            await window.ShowDialog(_owner);
            return selectedPath;

            void UpdateResults()
            {
                var text = query.Text?.Trim() ?? "";
                var filtered = files
                    .Where(path => text.Length == 0
                                   || Path.GetFileName(path).Contains(text, StringComparison.OrdinalIgnoreCase)
                                   || path.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path).Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(100)
                    .ToArray();
                results.ItemsSource = filtered;
                results.SelectedIndex = filtered.Length > 0 ? 0 : -1;
            }

            void AcceptSelection()
            {
                if (results.SelectedItem is not string path) return;
                selectedPath = path;
                window.Close();
            }
        }

        [RelayCommand]
        private async Task ReplaceWorkspaceAsync()
        {
            var plan = await PrepareWorkspaceReplaceAsync();
            if (plan is null || plan.LimitReached || plan.MatchCount == 0)
                return;

            if (!await ConfirmWorkspaceReplaceAsync(plan))
                return;

            var summary = ApplyWorkspaceReplacePlan(plan);
            var message = $"Replaced {summary.ReplacedMatches} match(es) in {summary.ReplacedFiles} file(s).";
            if (summary.ModifiedOpenDocuments > 0)
                message += $" {summary.ModifiedOpenDocuments} open document(s) now have unsaved changes.";
            if (summary.FailedFiles > 0)
                message += $" {summary.FailedFiles} file(s) changed since preview or couldn't be written.";
            SearchStatus = message;
            await SearchWorkspace();
            SearchStatus = message;
        }

        private async Task<bool> ConfirmWorkspaceReplaceAsync(WorkspaceReplacePlan plan)
        {
            var confirmed = false;
            Window? dialog = null;
            dialog = new Window
            {
                Title = "Confirm Replace All",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 16,
                    Children =
                          {
                              new TextBlock
                              {
                                  Text = $"Replace {plan.MatchCount} occurrence(s) in {plan.Files.Count} file(s)?",
                                  TextWrapping = TextWrapping.Wrap
                              },
                              new TextBlock
                              {
                                  Text = "Closed files will be saved immediately. Open documents will be changed in memory and remain unsaved.",
                                  TextWrapping = TextWrapping.Wrap
                              },
                              new StackPanel
                              {
                                  Orientation = Orientation.Horizontal,
                                  Spacing = 8,
                                  HorizontalAlignment = HorizontalAlignment.Right,
                                  Children =
                                  {
                                      MakeButton("Replace All", accept: true),
                                      MakeButton("Cancel", accept: false)
                                  }
                              }
                          }
                }
            };

            await dialog.ShowDialog(_owner);
            return confirmed;

            Button MakeButton(string text, bool accept)
            {
                var button = new Button { Content = text, MinWidth = 90 };
                button.Click += (_, _) =>
                {
                    confirmed = accept;
                    dialog?.Close();
                };
                return button;
            }
        }

        [RelayCommand]
        private async Task OpenSettings()
        {
            await new SettingsWindow(Settings).ShowDialog(_owner);
        }


        public void Dispose()
        {
            _outlineRequestCts?.Cancel();
            _outlineRequestCts?.Dispose();
            DocumentOutlineItems.CollectionChanged -= OnDocumentOutlineItemsChanged;
            _languageServers.Dispose();
            _workspace.Dispose();
            Run.Dispose();
        }
    }
}
