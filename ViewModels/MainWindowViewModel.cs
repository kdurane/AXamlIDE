using System;
using System.Collections.Concurrent;
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
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using AXamlIDE.CompletionEngine.Completion;
using AXamlIDE.Enums;
using AXamlIDE.Lsp;
using AXamlIDE.Models;
using AXamlIDE.Services;
using AXamlIDE.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextMateSharp.Grammars;

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
        private readonly DotNetProjectRunner _projectRunner = new();
        private NetCoreDbgSession? _debugSession;
        private string? _debugProjectPath;
        private readonly WorkspaceSearchService _workspaceSearch = new();
        private readonly ProblemNavigationService _problemNavigation = new();
        private readonly WorkspaceModel _workspace;
        private readonly XamlLanguageService _xaml;
        private readonly Dictionary<OpenDocument, Action> _breakpointHandlers = [];
        private bool _runStopRequested;
        private int _stoppedThreadId = 1;
        private CancellationTokenSource? _outlineRequestCts;
        private int _outlineRequestSequence;
        private readonly IStorageProvider _storageProvider;

        public ObservableCollection<ProjectTreeNode> WorkspaceRoots => _workspace.WorkspaceRoots;
        public ObservableCollection<OpenDocument> Documents => _documents.Documents;
        public ObservableCollection<string> BuildOutput { get; } = [];
        public ObservableCollection<string> ConsoleOutput { get; } = [];
        public ObservableCollection<ProblemItem> Problems { get; } = [];
        public ObservableCollection<WorkspaceSearchResult> SearchResults { get; } = [];
        public ObservableCollection<DocumentOutlineItem> DocumentOutlineItems { get; } = [];

        [ObservableProperty] public partial int SelectedToolTabIndex { get; set; } = 1;
        [ObservableProperty] public partial object? SelectedDocTabItem { get; set; }
        [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
        [ObservableProperty] public partial string ReplaceText { get; set; } = string.Empty;
        [ObservableProperty] private partial bool IsSearching { get; set; }
        [ObservableProperty] public partial string SearchStatus { get; set; } = "Search the current workspace.";
        [ObservableProperty] public partial bool IsRunning { get; set; }
        [ObservableProperty] public partial bool IsDebugStopped { get; set; }
        [ObservableProperty] public partial string DebugStatus { get; set; } = "No active debug session.";
        [ObservableProperty] private partial bool IsBusy { get; set; }
        [ObservableProperty] public partial string LspStatus { get; set; } = "language servers: idle";
        [ObservableProperty] public partial OpenDocument? ActiveDocument { get; set; }
        public bool IsEditorVisible => ActiveDocument is not null;
        public bool IsWelcomeVisible => ActiveDocument is null;
        public bool IsDebugging => _debugSession is not null;
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

        public MainWindowViewModel(SettingsViewModel settings, IStorageProvider storageProvider,
        Window owner, WorkspaceModel workspace, XamlLanguageService xaml)
        {
            Settings = settings;
            _storageProvider = storageProvider;
            _owner = owner;
            _workspace = workspace;
            _xaml = xaml;
            _projectRunner.OutputReceived += AppendProcessOutput;

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
        private string? FindProjectForDocument(string documentPath) => _workspace.FindProjectForDocument(documentPath);
        private string? ResolveProjectPath(ProjectTreeNode? selection) => _workspace.ResolveProjectPath(selection);

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
        private async Task ContinueDebuggingAsync()
        {
            if (_debugSession is null || !IsDebugStopped)
                return;
            try
            {
                await _debugSession.ContinueAsync(_stoppedThreadId);
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Couldn't continue debugging: {ex.Message}");
            }
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

        private string? _startupProjectPath;   // set from a "Set as Startup Project" menu item

        private string? ResolveStartupProject(ProjectTreeNode? selection)
        {
            // 1. Explicit startup project wins.
            if (_startupProjectPath is not null && File.Exists(_startupProjectPath))
                return _startupProjectPath;

            // 2. The selected project, if it can actually run.
            var selected = ResolveProjectPath(selection);   // fine with a null selection
            if (selected is not null && IsRunnable(selected))
                return selected;

            // 3. Otherwise, the only executable project in the workspace.
            var exes = _workspace.SolutionProjects.Where(IsRunnable).Distinct().ToList();
            return exes.Count == 1 ? exes[0] : null;
        }

        private bool IsRunnable(string projectPath)
            => Host.GetProjectOutputType(projectPath) is { } t
               && (t.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("WinExe", StringComparison.OrdinalIgnoreCase));



        [RelayCommand]
        private async Task BuildProject(ProjectTreeNode? selection)
        {
            var selected = selection ?? SelectedWorkspaceNode;
            string? projectPath;
            try
            {
                projectPath = ResolveProjectPath(selected);
            }
            catch (Exception ex)
            {
                ShowBuildMessage($"Could not determine the selected project: {ex}");
                return;
            }

            if (projectPath is null)
            {
                ShowBuildMessage("Select a project or one of its files in the workspace tree first.");
                return;
            }

            if (IsBusy)
                return;

            BeginBuildOutput($"Building {Path.GetFileName(projectPath)} (Debug)");
            IsBusy = true;
            try
            {
                var exitCode = await _projectRunner.BuildAsync(projectPath);
                AppendBuildOutput(exitCode == 0
                    ? "Build succeeded."
                    : $"Build failed with exit code {exitCode}.");
            }
            catch (Exception ex)
            {
                AppendBuildOutput($"Build failed: {ex}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RunProjectAsync(ProjectTreeNode? selection)
        {
            var selected = selection ?? SelectedWorkspaceNode;
            string? projectPath;
            try
            {
                projectPath = ResolveProjectPath(selected);
            }
            catch (Exception ex)
            {
                ShowBuildMessage($"Could not determine the selected project: {ex}");
                return;
            }

            if (projectPath is null)
            {
                ShowBuildMessage("Select a project or one of its files in the workspace tree first.");
                return;
            }

            if (IsBusy)
                return;

            BeginBuildOutput($"Building {Path.GetFileName(projectPath)} (Debug) before running");
            IsBusy = true;
            try
            {
                var buildExitCode = await _projectRunner.BuildAsync(projectPath);
                if (buildExitCode != 0)
                {
                    AppendBuildOutput($"Run cancelled because the build failed with exit code {buildExitCode}.");
                    return;
                }

                BeginRunOutput($"Running {Path.GetFileName(projectPath)} (Debug)");
                IsRunning = true;
                var runExitCode = await _projectRunner.RunAsync(projectPath);
                AppendConsoleOutput(_runStopRequested
                    ? "Application stopped."
                    : runExitCode == 0
                        ? "Application exited."
                        : $"Application exited with code {runExitCode}.");
            }
            catch (Exception ex)
            {
                AppendConsoleOutput($"Run failed: {ex}");
            }
            finally
            {
                IsRunning = false;
                _runStopRequested = false;
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task StartDebuggingAsync(ProjectTreeNode? selection)
        {
            var selected = selection ?? SelectedWorkspaceNode;
            string? projectPath;
            try
            {
                projectPath = ResolveStartupProject(selected);
            }
            catch (Exception ex)
            {
                ShowBuildMessage($"Could not determine the selected project: {ex}");
                return;
            }

            if (projectPath is null)
            {
                ShowBuildMessage("Select a project or one of its files in the workspace tree first.");
                return;
            }

            if (IsBusy || IsRunning)
                return;

            if (Documents.Any(document => document.IsDirty))
            {
                ShowStatusMessage("Save open document changes before debugging so the built code matches the editor.");
                return;
            }

            IsBusy = true;
            BeginBuildOutput($"Building {Path.GetFileName(projectPath)} (Debug) before debugging");
            try
            {
                var buildExitCode = await _projectRunner.BuildAsync(projectPath);
                if (buildExitCode != 0)
                {
                    AppendBuildOutput($"Debugging cancelled because the build failed with exit code {buildExitCode}.");
                    return;
                }

                var (programPath, workingDirectory) = await Task.Run(() => ResolveDebugProgram(projectPath));
                var session = new NetCoreDbgSession(NetCoreDbgSession.ResolveExecutablePath());
                _debugSession = session;
                _debugProjectPath = projectPath;
                OnPropertyChanged(nameof(IsDebugging));
                IsRunning = true;
                IsDebugStopped = false;
                DebugStatus = $"Starting {Path.GetFileName(programPath)}...";
                SelectedToolTabIndex = 2;
                session.OutputReceived += AppendConsoleOutput;
                session.EventReceived += message => OnDebugAdapterEvent(session, message);
                session.Failed += error => Dispatcher.UIThread.Post(() =>
                    EndDebugSession(session, $"Debugger failed: {error.Message}"));

                var projects = await Task.Run(() => GetProjectClosure(projectPath));

                var breakpoints = Documents
                    .Where(document => document is { LanguageId: "csharp", BreakpointLines.Count: > 0 }
                                       && FindProjectForDocument(document.Path) is { } p
                                       && projects.Contains(p))
                    .ToDictionary(
                        document => document.Path,
                        document => document.BreakpointLines,
                        PathComparer);

                await session.StartAsync(programPath, workingDirectory, breakpoints);
                if (ReferenceEquals(_debugSession, session))
                    DebugStatus = "Debugging. Execution will pause at enabled breakpoints.";
            }
            catch (Exception ex)
            {
                if (_debugSession is { } session)
                    EndDebugSession(session, $"Couldn't start debugging: {ex.Message}");
                else
                    ShowBuildMessage($"Couldn't start debugging: {ex.Message}");
                AppendConsoleOutput($"Debug launch failed: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private HashSet<string> GetProjectClosure(string rootProject)
        {
            var result = new HashSet<string>(PathComparer);
            var stack = new Stack<string>();
            stack.Push(rootProject);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!result.Add(current)) continue;
                foreach (var reference in Host.GetProjectReferences(current))
                {
                    // may need the same backslash fix, and resolving relative to current's directory
                    var normalized = reference.Replace('\\', Path.DirectorySeparatorChar);
                    var full = Path.IsPathRooted(normalized)
                        ? normalized
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, normalized));
                    stack.Push(full);
                }
            }
            return result;
        }

        [RelayCommand]
        private void StopRunningProject()
        {
            if (_debugSession is { } session)
            {
                _ = StopDebugSessionAsync(session);
                return;
            }

            if (!IsRunning)
                return;

            _runStopRequested = true;
            AppendConsoleOutput("Stopping application...");
            _projectRunner.Stop();
        }

        private async Task StopDebugSessionAsync(NetCoreDbgSession session)
        {
            DebugStatus = "Stopping debug session...";
            try
            {
                await session.StopAsync();
            }
            catch (Exception ex)
            {
                AppendConsoleOutput($"Couldn't stop debugger cleanly: {ex.Message}");
            }
            finally
            {
                EndDebugSession(session, "Debug session stopped.");
            }
        }

        private (string ProgramPath, string WorkingDirectory) ResolveDebugProgram(string projectPath)
        {
            var assemblyName = Host.GetProjectAssemblyName(projectPath);
            var outputPath = Host.GetProjectOutputPath(projectPath);
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Project '{projectPath}' has no parent directory.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException($"MSBuild did not provide an output path for '{projectPath}'.");

            var outputDirectory = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.GetFullPath(Path.Combine(projectDirectory, outputPath));
            var programPath = Path.Combine(outputDirectory, assemblyName + ".dll");
            if (!File.Exists(programPath)) Console.WriteLine($"The built application assembly wasn't found at '{programPath}'.", programPath);

            return (programPath, projectDirectory);
        }

        private void OnDebugAdapterEvent(NetCoreDbgSession session, JsonElement message)
        {
            if (!message.TryGetProperty("event", out var eventName))
                return;

            AppendConsoleOutput(message.GetRawText());

            switch (eventName.GetString())
            {
                case "output":
                    if (message.TryGetProperty("body", out var outputBody)
                        && outputBody.TryGetProperty("output", out var output))
                        AppendConsoleOutput(output.GetString() ?? string.Empty);
                    break;
                case "stopped":
                    if (!message.TryGetProperty("body", out var stoppedBody))
                        return;
                    var reason = stoppedBody.TryGetProperty("reason", out var reasonElement)
                        ? reasonElement.GetString() ?? "breakpoint"
                        : "breakpoint";
                    var threadId = stoppedBody.TryGetProperty("threadId", out var threadElement)
                        ? threadElement.GetInt32()
                        : 1;
                    _stoppedThreadId = threadId;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ReferenceEquals(_debugSession, session))
                            return;
                        IsDebugStopped = true;
                        DebugStatus = $"Paused ({reason}).";
                    });
                    _ = NavigateToTopFrameAsync(session, threadId);
                    break;
                case "continued":
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_debugSession, session))
                        {
                            IsDebugStopped = false;
                            DebugStatus = "Debugging.";
                        }
                    });
                    break;
                case "terminated":
                    Dispatcher.UIThread.Post(() => EndDebugSession(session, "Debug session ended."));
                    break;
                case "exited":
                    var code = message.TryGetProperty("body", out var exitedBody)
                               && exitedBody.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : -1;
                    AppendConsoleOutput($"Debuggee exited with code {code}.");
                    break;
            }
        }

        private async Task NavigateToTopFrameAsync(NetCoreDbgSession session, int threadId)
        {
            try
            {
                var frame = await session.GetTopFrameAsync(threadId);
                if (frame is { } location && File.Exists(location.Path))
                    Dispatcher.UIThread.Post(() => DebugLocationChanged?.Invoke(location.Path, location.Line));
            }
            catch (Exception ex)
            {
                AppendConsoleOutput($"Couldn't retrieve the stopped location: {ex.Message}");
            }
        }

        private void EndDebugSession(NetCoreDbgSession session, string message)
        {
            if (!ReferenceEquals(_debugSession, session))
                return;

            _debugSession = null;
            _debugProjectPath = null;
            session.Dispose();
            OnPropertyChanged(nameof(IsDebugging));
            IsDebugStopped = false;
            IsRunning = false;
            DebugStatus = message;
            AppendConsoleOutput(message);
        }

        private void BeginBuildOutput(string message)
        {
            BuildOutput.Clear();
            SelectedToolTabIndex = 1;
            AppendBuildOutput(message);
        }

        private void BeginRunOutput(string message)
        {
            ConsoleOutput.Clear();
            SelectedToolTabIndex = 3;
            AppendConsoleOutput(message);
        }

        private void ShowBuildMessage(string message)
        {
            BeginBuildOutput(message);
        }

        private void AppendBuildOutput(string message)
        {
            if (Dispatcher.UIThread.CheckAccess())
                BuildOutput.Add(message);
            else
                Dispatcher.UIThread.Post(() => BuildOutput.Add(message));
        }

        private void AppendConsoleOutput(string message)
        {
            if (Dispatcher.UIThread.CheckAccess())
                ConsoleOutput.Add(message);
            else
                Dispatcher.UIThread.Post(() => ConsoleOutput.Add(message));
        }

        private void AppendProcessOutput(string message, bool isRun)
        {
            if (isRun)
                AppendConsoleOutput(message);
            else
                AppendBuildOutput(message);
        }

        private static StringComparer PathComparer
            => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static bool PathsEqual(string? left, string? right)
            => left is not null && right is not null && PathComparer.Equals(left, right);



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
            Action handler = () => OnDocumentBreakpointsChanged(document);
            _breakpointHandlers[document] = handler;
            document.BreakpointsChanged += handler;
            if (!document.IsReadOnly && _languageServers.Handles(document.LanguageId))
                _ = AttachLspAsync(document);
        }

        private void OnDocumentBreakpointsChanged(OpenDocument document)
        {
            if (_debugSession is not { } session || document.LanguageId != "csharp")
                return;
            if (_debugProjectPath is null
                || !PathsEqual(FindProjectForDocument(document.Path), _debugProjectPath))
                return;

            _ = UpdateDebugBreakpointsAsync(session, document.Path, document.BreakpointLines);
        }

        private async Task UpdateDebugBreakpointsAsync(
            NetCoreDbgSession session,
            string path,
            IReadOnlyList<int> lines)
        {
            try
            {
                await session.SetBreakpointsAsync(path, lines);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => ShowStatusMessage(
                    $"Couldn't update breakpoints for '{Path.GetFileName(path)}': {ex.Message}"));
            }
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
            if (_breakpointHandlers.Remove(document, out var handler))
                document.BreakpointsChanged -= handler;
            if (_debugSession is { } session && document.LanguageId == "csharp")
                _ = UpdateDebugBreakpointsAsync(session, document.Path, []);
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
            if (_debugSession is not null)
                StopRunningProject();

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
            _debugSession?.Dispose();
            _debugSession = null;
            _projectRunner.Dispose();
        }
    }
}
