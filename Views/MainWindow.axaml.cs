using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using AXaxmlIDE.Enums;
using AXaxmlIDE.Lsp;
using AXaxmlIDE.Models;
using AXaxmlIDE.Services;
using AXaxmlIDE.ViewModels;
using TextMateSharp.Grammars;
using AXaxmlIDE.EditorFeatures;
using static AXaxmlIDE.EditorFeatures.TextMateController;

// ReSharper disable AsyncVoidEventHandlerMethod

namespace AXaxmlIDE.Views;

public partial class MainWindow : Window
{
    private readonly TextMate.Installation _textMate;
    private readonly CustomMargin? _customMargin;
    private readonly DispatcherTimer _outlineTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };

    private readonly PreviewPaneViewModel _preview;

    private readonly DiagnosticRenderer _diagnosticRenderer = new();
    private OpenDocument? _active;

    private bool _treeOnRight;
    private bool _explorerVisible = true;

    private readonly DialogService _dialogs;
    private readonly EditorContext _ctx;
    private readonly FoldingController _folding;
    private readonly OccurrenceController _occurrences;
    private readonly WorkspaceEditService _edits;
    private readonly CodeActionController _codeActions;
    private readonly HoverController _hover;
    private readonly SignatureHelpController _signature;
    private readonly CompletionController _completion;
    private readonly TypingController _typing;
    private readonly EditorNavigator _navigator;
    private readonly NavigationCommands _nav;
    private readonly TextMateController _textMateController;

    public MainWindow()
    {
        InitializeComponent();
        _ctx = new EditorContext(Editor, () => ViewModel)
        {
            ActivateDocument = doc => DocTabs.SelectedItem = doc
        };
        _preview = new PreviewPaneViewModel(path => ViewModel.GetXamlPreviewTargetAsync(path));
        PreviewPane.DataContext = _preview;
        _dialogs = new DialogService(this);

        // TreeViewItem.Expanded bubbles, so one handler on the TreeView catches every node.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerPressedEvent, OnFileTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
    
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        _occurrences = new OccurrenceController(_ctx);
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateStatus();
        };

        Editor.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        Editor.TextArea.RightClickMovesCaret = true;
        Editor.Options.EnableTextDragDrop = true;
        Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

        _customMargin = new CustomMargin();
        _edits = new WorkspaceEditService(_ctx);
        _codeActions = new CodeActionController(_ctx, _edits);
        _hover = new HoverController(_ctx, _codeActions);
        _navigator = new EditorNavigator(_ctx);
        _nav = new NavigationCommands(_ctx, _dialogs, _edits, _navigator);
        _signature = new SignatureHelpController(_ctx);
        _completion = new CompletionController(_ctx, _hover);
        _typing = new TypingController(_ctx, _hover, _completion, _signature);

        var settingsStore = new EditorSettingsStore();
        var settingsLoad = settingsStore.Load();
        var settings = settingsLoad.Settings;
        _textMate = Editor.InstallTextMate(registryOptions);
        _textMateController = new TextMateController(_ctx, _textMate, registryOptions, _customMargin);
        _textMateController.ThemeApplied += ApplyThemeColorsToWindow;
        //_textMate.SetTheme(ThemeCatalog.Find(settings.Theme.ToString()).Load(registryOptions));
        var viewModel = new MainWindowViewModel(_textMate, registryOptions, settings, settingsStore, StorageProvider, this);
        _textMate.SetTheme(viewModel.SelectedTheme.Load(registryOptions));
        DataContext = viewModel;
        viewModel.EditorSettingsChanged += ApplyEditorSettings;
        viewModel.SolutionExplorerSideChanged += ApplySolutionExplorerSide;
        viewModel.StatusMessageChanged += viewModel.ShowStatusMessage;
        viewModel.DiagnosticsChanged += OnDiagnosticsChanged;
        viewModel.NavigateToProblemRequested += _navigator.ToProblem;
        viewModel.DebugLocationChanged += _navigator.ToDebugLocation;
        viewModel.WorkspaceEditRequested = _edits.ApplyServerEditAsync;
        ApplyEditorSettings();
        ApplySolutionExplorerSide(settings.SolutionExplorerOnRight);
        if (settingsLoad.Warnings.Count > 0)
            viewModel.ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));

        _outlineTimer.Tick += OnOutlineTimer;
        viewModel.DocumentLanguageClientAttached += OnDocumentLanguageClientAttached;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        Closed += (_, _) =>
        {
            _outlineTimer.Stop();
            _customMargin?.SetDocument(null);
            viewModel.Dispose();
            _textMate.Dispose();
            _folding?.Dispose();
            _preview.Dispose();
        };

        UpdateStatus();

        _folding = new FoldingController(_ctx);
        _ctx.TextChanged += _ => OnActiveDocumentChanged();
    }

    private void ApplyEditorSettings()
    {
        var settings = ViewModel.EditorFontFamily;
        Editor.FontFamily = new FontFamily(settings);
        Editor.FontSize = ViewModel.EditorFontSize;
        Editor.ShowLineNumbers = ViewModel.ShowLineNumbers;
        Editor.Options.HighlightCurrentLine = ViewModel.HighlightCurrentLine;
    }
    // ================================================================== tree


    private async void OnNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: ProjectTreeNode { NeedsPopulating: true } node })
            return;

        await node.PopulateAsync(); // project evaluation runs off the UI thread
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();

        var node = item?.DataContext as ProjectTreeNode
                   ?? FileTree.SelectedItem as ProjectTreeNode;
        if (node is not { Kind: NodeKind.File }
            || string.IsNullOrEmpty(node.FullPath))
            return;

        e.Handled = true;
        if (node.Children.Count > 0 && item != null)
        {
            item.IsExpanded = true;
            node.IsExpanded = true;
        }

        try
        {
            DocTabs.SelectedItem = ViewModel.OpenDocument(node.FullPath);
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
    }

    private async void OnCodeActionsClick(object? sender, RoutedEventArgs e)
        => await _codeActions.ShowCodeActionsAsync(anchor: Editor);

    private void OnFileTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(FileTree);
        if (!point.Properties.IsRightButtonPressed)
            return;

        var control = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (control is not { DataContext: ProjectTreeNode node })
            return;

        control.IsSelected = true;
        var menu = new ContextMenu();
        if (node.Kind == NodeKind.File)
            menu.Items.Add(CreateTreeMenuItem("Open", () => OpenTreeFile(node)));
        if (!string.IsNullOrEmpty(node.FullPath))
            menu.Items.Add(CreateTreeMenuItem("Copy Path", () => CopyTreePathAsync(node)));
        if (node.Kind is NodeKind.Directory or NodeKind.Solution or NodeKind.Project or NodeKind.Folder)
            menu.Items.Add(CreateTreeMenuItem("Refresh", () => RefreshTreeNodeAsync(node)));
        if (node.Kind == NodeKind.Project
            || node.Kind == NodeKind.File && node.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "Build",
                Command = ViewModel.BuildProjectCommand,
                CommandParameter = node
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Run",
                Command = ViewModel.RunProjectCommand,
                CommandParameter = node
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Start Debugging",
                Command = ViewModel.StartDebuggingCommand,
                CommandParameter = node
            });
        }

        if (menu.Items.Count == 0)
            return;

        control.ContextMenu = menu;
        menu.Open(control);
        e.Handled = true;
    }

    private MenuItem CreateTreeMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private Task OpenTreeFile(ProjectTreeNode node)
    {
        try
        {
            DocTabs.SelectedItem = ViewModel.OpenDocument(node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.ShowStatusMessage($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private async Task CopyTreePathAsync(ProjectTreeNode node)
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                throw new InvalidOperationException("The clipboard is unavailable.");
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(node.FullPath));
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ViewModel.ShowStatusMessage($"Couldn't copy path: {ex.Message}");
        }
    }

    private async Task RefreshTreeNodeAsync(ProjectTreeNode node)
    {
        try
        {
            if ((node.Kind is NodeKind.Directory or NodeKind.Solution)
                && ReferenceEquals(node, ViewModel.WorkspaceRoots.FirstOrDefault()))
                ViewModel.RefreshWorkspace();
            else
                await ViewModel.RefreshWorkspaceNodeAsync(node);
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Couldn't refresh '{node.Name}': {ex.Message}");
        }
    }

    private void ApplySolutionExplorerSide(bool treeOnRight)
    {
        _treeOnRight = treeOnRight;
        ContentGrid.ColumnDefinitions.Clear();
        if (_treeOnRight)
        {
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(_explorerVisible ? 4 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(_explorerVisible ? 260 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(30, GridUnitType.Pixel));

            Grid.SetColumn(EditorHost, 0);
            Grid.SetColumn(Splitter, 1);
            Grid.SetColumn(ExplorerPane, 2);
            Grid.SetColumn(ActivityBar, 3);
        }
        else
        {
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(30, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(_explorerVisible ? 260 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(_explorerVisible ? 4 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            Grid.SetColumn(ExplorerPane, 1);
            Grid.SetColumn(Splitter, 2);
            Grid.SetColumn(EditorHost, 3);
            Grid.SetColumn(ActivityBar, 0);
        }

        ExplorerPane.IsVisible = _explorerVisible;
        Splitter.IsVisible = _explorerVisible;
        ActivityBar.IsVisible = true;
    }

    private void OnToggleExplorerClick(object? sender, RoutedEventArgs e)
    {
        _explorerVisible = !_explorerVisible;
        ApplySolutionExplorerSide(_treeOnRight);
    }


    private void OnDocumentLanguageClientAttached(OpenDocument document)
    {
        if (ReferenceEquals(document, _active))
            Dispatcher.UIThread.Post(QueueOutlineUpdate);
    }

    private void QueueOutlineUpdate()
    {
        _outlineTimer.Stop();
        _outlineTimer.Start();
    }

    private void OnOutlineTimer(object? sender, EventArgs e)
    {
        _outlineTimer.Stop();
        if (_active is { } document)
            _ = ViewModel.UpdateDocumentOutlineAsync(document);
        else
            ViewModel.ClearDocumentOutline();
    }


    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => _navigator.ToSelectedOutlineItem();

    // ================================================================= tabs

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _hover.Hide(resetOffset: true);

        if (_active is { } prev) // save view state of the tab we're leaving
        {
            prev.CaretOffset = Editor.CaretOffset;
            prev.VerticalOffset = Editor.VerticalOffset;
            prev.HorizontalOffset = Editor.HorizontalOffset;
        }

        // FoldingManager installs document-bound generators; detach them while
        // the old document is still attached to the editor.
        _ctx.Detach();
        _active = DocTabs.SelectedItem as OpenDocument;
        ViewModel.ActiveDocument = _active;
        _customMargin?.SetDocument(_active);

        if (_active is null)
        {
            _outlineTimer.Stop();
            ViewModel.ClearDocumentOutline();
            _preview.SetDocument(null);
            Editor.Document = new TextDocument();
            Editor.IsReadOnly = false;
            _diagnosticRenderer.Segments = null;
            _ctx.Attach(null);
            _textMateController.SetGrammarForFile("");
            UpdateStatus();
            return;
        }

        var doc = _active;
        ViewModel.ClearDocumentOutline();
        QueueOutlineUpdate();
        UpdatePreviewPaneVisibility(doc);
        Editor.Document = doc.Document; // TextMate re-binds on DocumentChanged
        Editor.IsReadOnly = doc.IsReadOnly;
        _ctx.Attach(doc);
        _diagnosticRenderer.Segments = doc.Diagnostics;
        _textMateController.SetGrammarForFile(doc.Path);
        Editor.CaretOffset = Math.Min(doc.CaretOffset, doc.Document.TextLength);

        // Scrolling needs a layout pass first.
        Dispatcher.UIThread.Post(() =>
        {
            Editor.ScrollToVerticalOffset(doc.VerticalOffset);
            Editor.ScrollToHorizontalOffset(doc.HorizontalOffset);
        }, DispatcherPriority.Loaded);

        Editor.Focus();
        UpdateStatus();
        _preview.SetDocument(doc);
    }

    private void NavigateToDebugLocation(string path, int line)
    {
        OpenDocument document;
        try
        {
            document = ViewModel.OpenDocument(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.ShowStatusMessage($"Couldn't open the stopped source location: {ex.Message}");
            return;
        }

        DocTabs.SelectedItem = document;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_active, document))
                return;
            var lineNumber = Math.Clamp(line, 1, document.Document.LineCount);
            Editor.CaretOffset = document.Document.GetLineByNumber(lineNumber).Offset;
            Editor.ScrollToLine(lineNumber);
            Editor.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void UpdatePreviewPaneVisibility(OpenDocument? document)
    {
        var visible = document?.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true;
        DocumentSurface.ColumnDefinitions[1].Width = visible
            ? new GridLength(4)
            : new GridLength(0);
        DocumentSurface.ColumnDefinitions[2].Width = visible
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        DocumentSurface.ColumnDefinitions[2].MinWidth = visible ? 280 : 0;
        PreviewSplitter.IsVisible = visible;
        PreviewPane.IsVisible = visible;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        var editorPoint = e.GetPosition(Editor);
        if (editorPoint is { X: >= 0, Y: >= 0 }
            && editorPoint.X < Editor.Bounds.Width && editorPoint.Y < Editor.Bounds.Height)
        {
            var nextSize = ViewModel.EditorFontSize + (e.Delta.Y > 0 ? 1 : -1);
            ViewModel.EditorFontSize = Math.Clamp(nextSize, 6, 72);
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }


    // private readonly DispatcherTimer _foldingTimer =
    //     new() { Interval = TimeSpan.FromMilliseconds(250) };

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is OpenDocument doc) TryClose(doc);
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle
            && (sender as Control)?.DataContext is OpenDocument doc)
            TryClose(doc);
    }

    private async void TryClose(OpenDocument doc)
    {
        var index = ViewModel.Documents.IndexOf(doc);

        if (doc.IsDirty)
        {
            switch (await _dialogs.AskSaveAsync(doc))
            {
                case SaveChoice.Cancel: return;
                case SaveChoice.Save: await ViewModel.SaveDocumentAsync(doc); break;
            }
        }

        ViewModel.CloseDocument(doc);

        if (ViewModel.Documents.Count > 0 && DocTabs.SelectedItem is null)
            DocTabs.SelectedIndex = Math.Min(index, ViewModel.Documents.Count - 1);
    }


    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e)
        {
            case { Key: Key.F8, KeyModifiers: KeyModifiers.None }:
                ViewModel.NavigateDiagnosticCommand.Execute(true);
                e.Handled = true;
                return;
            case { Key: Key.F8, KeyModifiers: KeyModifiers.Shift }:
                ViewModel.NavigateDiagnosticCommand.Execute(false);
                e.Handled = true;
                return;
            case { Key: Key.F12, KeyModifiers: KeyModifiers.None }:
                _ = _nav.GoToDefinitionAsync();
                e.Handled = true;
                return;
            case { Key: Key.F12, KeyModifiers: KeyModifiers.Shift }:
                _ = _nav.FindReferencesAsync();
                e.Handled = true;
                return;
            case { Key: Key.F, KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift) }:
                ShowFindInFiles();
                e.Handled = true;
                return;
            case { Key: Key.F5, KeyModifiers: KeyModifiers.None }:
                ViewModel.StartDebuggingCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { Key: Key.F5, KeyModifiers: KeyModifiers.Control }:
                ViewModel.RunProjectCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { Key: Key.F5, KeyModifiers: KeyModifiers.Shift }:
                ViewModel.StopRunningProjectCommand.Execute(null);
                e.Handled = true;
                return;
            case { Key: Key.B, KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift) }:
                ViewModel.BuildProjectCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift), Key: Key.Space }:
                {
                    if (_active is { } activeDocument)
                        _ = _signature.RequestAsync(activeDocument, triggerCharacter: null);
                    e.Handled = true;
                    return;
                }
            case { KeyModifiers: KeyModifiers.Control, Key: Key.OemPeriod }:
                _ = _codeActions.ShowCodeActionsAsync();
                e.Handled = true;
                return;
        }

        if (e.KeyModifiers != KeyModifiers.Control) return;

        switch (e.Key)
        {
            case Key.P:
                _ = _nav.QuickOpenAsync();
                e.Handled = true;
                break;
            case Key.T:
                _ = _nav.SearchWorkspaceSymbolsAsync();
                e.Handled = true;
                break;
            case Key.R:
                _ = _nav.RenameSymbolAsync();
                e.Handled = true;
                break;
            case Key.S when _active is not null:
                ViewModel.SaveActiveDocumentCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W when _active is { } doc:
                TryClose(doc);
                e.Handled = true;
                break;
            case Key.Space when _active is { Sync: not null } doc:
                _ = _completion.ShowCompletionAsync(doc, triggerChar: null);
                e.Handled = true;
                break;
        }
    }

    // ================================================================== LSP

    private void OnSignatureHelpClick(object? sender, RoutedEventArgs e)
    {
        if (_active is { } document)
            _ = _signature.RequestAsync(document, triggerCharacter: null);
    }

    private void OnProblemClick(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is ProblemItem problem)
            _navigator.ToProblem(problem);
    }


    private void OnFindInFilesClick(object? sender, RoutedEventArgs e) => ShowFindInFiles();

    private void ShowFindInFiles()
    {
        ToolTabs.SelectedIndex = 4;
        FindInFilesQuery.Focus();
        FindInFilesQuery.SelectAll();
    }

    private async void OnWorkspaceSymbolsClick(object? sender, RoutedEventArgs e)
        => await _nav.SearchWorkspaceSymbolsAsync();

    private async void OnGoToDefinitionClick(object? sender, RoutedEventArgs e)
        => await _nav.GoToDefinitionAsync();

    private async void OnFindReferencesClick(object? sender, RoutedEventArgs e)
        => await _nav.FindReferencesAsync();

    private async void OnRenameSymbolClick(object? sender, RoutedEventArgs e)
        => await _nav.RenameSymbolAsync();


    private async void OnFindInFilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await ViewModel.SearchWorkspaceCommand.ExecuteAsync(true);
    }

    private async void OnReplaceWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await ViewModel.ReplaceWorkspaceCommand.ExecuteAsync(true);
    }

    private void OnSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: WorkspaceSearchResult result })
            _navigator.ToSearchResult(result);
    }

    private void OnDiagnosticsChanged(OpenDocument document)
    {
        if (!ReferenceEquals(document, _active)) return;
        _codeActions.RefreshMargin(document);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        UpdateStatus();
    }
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    // ============================================================== status bar

    private void UpdateStatus()
    {
        var caret = Editor.TextArea.Caret;
        ViewModel.ActiveCaretOffset = caret.Offset;
        CaretStatus.Text = _active is null ? "" : $"Ln {caret.Line}, Col {caret.Column}";
        DiagStatus.Text = _active is null ? "" : $"{_active.ErrorCount} errors, {_active.WarningCount} warnings";
    }


    private void OnActiveDocumentChanged()
    {
        if (_active is not { } doc) return;
        if (doc.LanguageId == "csharp")
            QueueOutlineUpdate();
    }


    private void ApplyThemeColorsToWindow(TextMate.Installation e)
    {
        if (!ApplyBrushAction(e, "statusBar.background", brush => StatusBar.Background = brush))
        {
            StatusBar.Background = Brushes.Purple;
        }

        if (!ApplyBrushAction(e, "statusBar.foreground", brush => LspStatus.Foreground = brush))
        {
            LspStatus.Foreground = Brushes.White;
        }

        //Applying the Editor background to the whole window for demo sake.
        ApplyBrushAction(e, "editor.background", brush => Background = brush);
        ApplyBrushAction(e, "editor.foreground", brush => Foreground = brush);
    }
}
