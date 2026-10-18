using System;
using System.Collections.Generic;
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
using AvaloniaEdit.TextMate;
using AXamlIDE.Enums;
using AXamlIDE.Interfaces;
using AXamlIDE.Models;
using AXamlIDE.Services;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using TextMateSharp.Grammars;
using static AXamlIDE.EditorFeatures.TextMateController;

// ReSharper disable AsyncVoidEventHandlerMethod

namespace AXamlIDE.Views;

public partial class MainWindow : Window,IDocumentHost
{
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    OpenDocument IDocumentHost.OpenReadOnly(string path, string text, string languageId)
        => ViewModel.OpenReadOnlyDocument(path, text, languageId);
    OpenDocument IDocumentHost.Open(string path) => ViewModel.OpenDocument(path);
    
    Task<IReadOnlyList<string>> IDocumentHost.GetWorkspaceFilesAsync()
        => ViewModel.GetWorkspaceFilesAsync();
    
    public void Activate(OpenDocument doc) => DocTabs.SelectedItem = doc;

    private readonly DispatcherTimer _outlineTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly PreviewPaneViewModel _preview;
    private OpenDocument? _active;
    private bool _treeOnRight;
    private bool _explorerVisible = true;
    private readonly IDialogService _dialogs;

    private DebugToolbarWindow? _debugToolbar;
    private PixelPoint? _debugToolbarPosition;

    public MainWindow()
    {
        InitializeComponent();

        _dialogs = new DialogService(this);

        var settingsStore = new EditorSettingsStore();
        var settingsLoad = settingsStore.Load();
        var settings = settingsLoad.Settings;
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var settingsVm = new SettingsViewModel(settings, settingsStore); // must implement IEditorSettings
        var workspace = new WorkspaceModel();
        var xaml = new XamlLanguageService(workspace);

        // ---- listen to the editor BEFORE Initialize so nothing it raises is missed.
        // The editor talks to us only through these three messages.
        _messenger.Register<MainWindow, StatusMessage>(this,
            static (w, m) => (w.DataContext as MainWindowViewModel)?.ShowStatusMessage(m.Text));
        _messenger.Register<MainWindow, EditorStateChangedMessage>(this,
            static (w, _) => { if (w.DataContext is MainWindowViewModel) w.UpdateStatus(); });
        _messenger.Register<MainWindow, EditorThemeAppliedMessage>(this,
            static (w, m) => w.ApplyThemeColorsToWindow(m.Installation));

        EditorPane.Initialize(registryOptions,
            new EditorServices(settingsVm, xaml, this, _dialogs, _messenger));
        EditorPane.Context.TextChanged += _ => OnActiveDocumentChanged();

        _preview = new PreviewPaneViewModel(xaml.GetPreviewTargetAsync);
        PreviewPane.DataContext = _preview;

        // TreeViewItem.Expanded bubbles, so one handler on the TreeView catches every node.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerPressedEvent, OnFileTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        var viewModel = new MainWindowViewModel(settingsVm, workspace, xaml, _dialogs);
        EditorPane.TextMateInstallation.SetTheme(settingsVm.SelectedTheme.Load(registryOptions));
        DataContext = viewModel;

        settingsVm.ThemeChanged += theme => EditorPane.TextMateInstallation.SetTheme(theme.Load(registryOptions));
        settingsVm.SolutionExplorerSideChanged += ApplySolutionExplorerSide;
        settingsVm.StatusMessageChanged += viewModel.ShowStatusMessage;

        viewModel.DiagnosticsChanged += doc => _messenger.Send(new DiagnosticsChangedMessage(doc));
        viewModel.NavigateToProblemRequested += p => _messenger.Send(new NavigateToProblemMessage(p));
        viewModel.Run.DebugLocationChanged += (path, line) => _messenger.Send(new NavigateToDebugLocationMessage(path, line));
        viewModel.Run.DebugSnapshotChanged += s => _messenger.Send(new DebugSnapshotChangedMessage(s));
        viewModel.WorkspaceEditRequested = EditorPane.WorkspaceEdits.ApplyServerEditAsync;

        ApplySolutionExplorerSide(settings.SolutionExplorerOnRight);

        if (settingsLoad.Warnings.Count > 0)
            viewModel.ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));

        _outlineTimer.Tick += OnOutlineTimer;
        viewModel.DocumentLanguageClientAttached += OnDocumentLanguageClientAttached;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        Closed += (_, _) =>
        {
            _outlineTimer.Stop();
            _preview.Dispose();
            EditorPane.Shutdown();
            _messenger.UnregisterAll(this);
            viewModel.Dispose();
            EditorPane.TextMateInstallation.Dispose();
        };

        UpdateStatus();

        viewModel.Run.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(RunViewModel.IsDebugging))
        UpdateDebugToolbar(viewModel.Run);
};
    }

    // ================================================================== tree

    private async void OnNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: ProjectTreeNode { NeedsPopulating: true } node })
            return;

        await node.PopulateAsync();
    }


    private async void OnQuickOpenClick(object? sender, RoutedEventArgs e)
        => await EditorPane.Navigation.QuickOpenAsync();

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
            this.OpenAndActivate(node.FullPath);
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
    }

    private async void OnCodeActionsClick(object? sender, RoutedEventArgs e)
        => await EditorPane.CodeActions.ShowCodeActionsAsync(anchor: EditorPane.EditorControl);

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
                Command = ViewModel.Run.BuildProjectCommand,
                CommandParameter = node
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Run",
                Command = ViewModel.Run.RunProjectCommand,
                CommandParameter = node
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Start Debugging",
                Command = ViewModel.Run.StartDebuggingCommand,
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
            this.OpenAndActivate(node.FullPath);
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
    {
        if (ViewModel.SelectedDocumentOutlineItem is { } item)
            _messenger.Send(new NavigateToOutlineItemMessage(item));
    }

    // ================================================================= tabs

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _active = DocTabs.SelectedItem as OpenDocument;
        ViewModel.ActiveDocument = _active;

        EditorPane.SetDocument(_active);

        ViewModel.ClearDocumentOutline();

        if (_active is null)
        {
            _outlineTimer.Stop();
            _preview.SetDocument(null);
            UpdateStatus();
            return;
        }

        QueueOutlineUpdate();
        UpdatePreviewPaneVisibility(_active);
        _preview.SetDocument(_active);
        UpdateStatus();
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

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: OpenDocument doc }) TryClose(doc);
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle
            && sender is Control { DataContext: OpenDocument doc })
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
                _ = EditorPane.Navigation.GoToDefinitionAsync();
                e.Handled = true;
                return;
            case { Key: Key.F12, KeyModifiers: KeyModifiers.Shift }:
                _ = EditorPane.Navigation.FindReferencesAsync();
                e.Handled = true;
                return;
            case { Key: Key.F, KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift) }:
                ShowFindInFiles();
                e.Handled = true;
                return;
            case { Key: Key.F5, KeyModifiers: KeyModifiers.None }:
                ViewModel.Run.StartDebuggingCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { Key: Key.F5, KeyModifiers: KeyModifiers.Control }:
                ViewModel.Run.RunProjectCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { Key: Key.F5, KeyModifiers: KeyModifiers.Shift }:
                if (ViewModel.Run.IsDebugging)
                    ViewModel.Run.ContinueDebuggingCommand.Execute(null);
                else
                    ViewModel.Run.StartDebuggingCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { Key: Key.F10, KeyModifiers: KeyModifiers.None } when ViewModel.Run.IsDebugging:
                ViewModel.Run.StepOverCommand.Execute(null);
                e.Handled = true;
                return;
            case { Key: Key.F11, KeyModifiers: KeyModifiers.None } when ViewModel.Run.IsDebugging:
                ViewModel.Run.StepIntoCommand.Execute(null);
                e.Handled = true;
                return;
            case { Key: Key.F11, KeyModifiers: KeyModifiers.Shift } when ViewModel.Run.IsDebugging:
                ViewModel.Run.StepOutCommand.Execute(null);
                e.Handled = true;
                return;
            case { Key: Key.B, KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift) }:
                ViewModel.Run.BuildProjectCommand.Execute(ViewModel.SelectedWorkspaceNode);
                e.Handled = true;
                return;
            case { KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift), Key: Key.Space }:
                {
                    if (_active is { } activeDocument)
                        _ = EditorPane.SignatureHelp.RequestAsync(activeDocument, triggerCharacter: null);
                    e.Handled = true;
                    return;
                }
            case { KeyModifiers: KeyModifiers.Control, Key: Key.OemPeriod }:
                _ = EditorPane.CodeActions.ShowCodeActionsAsync();
                e.Handled = true;
                return;
        }

        if (e.KeyModifiers != KeyModifiers.Control) return;

        switch (e.Key)
        {
            case Key.P:
                _ = EditorPane.Navigation.QuickOpenAsync();
                e.Handled = true;
                break;
            case Key.T:
                _ = EditorPane.Navigation.SearchWorkspaceSymbolsAsync();
                e.Handled = true;
                break;
            case Key.R:
                _ = EditorPane.Navigation.RenameSymbolAsync();
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
                _ = EditorPane.Completion.ShowCompletionAsync(doc, triggerChar: null);
                e.Handled = true;
                break;
        }
    }

    // ================================================================== LSP

    private void OnSignatureHelpClick(object? sender, RoutedEventArgs e)
    {
        if (_active is { } document)
            _ = EditorPane.SignatureHelp.RequestAsync(document, triggerCharacter: null);
    }

    private void OnProblemClick(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProblemItem problem })
            _messenger.Send(new NavigateToProblemMessage(problem));
    }


    private void OnFindInFilesClick(object? sender, RoutedEventArgs e) => ShowFindInFiles();

    private void ShowFindInFiles()
    {
        ToolTabs.SelectedIndex = ToolTab.Search;
        FindInFilesQuery.Focus();
        FindInFilesQuery.SelectAll();
    }

    private async void OnWorkspaceSymbolsClick(object? sender, RoutedEventArgs e)
        => await EditorPane.Navigation.SearchWorkspaceSymbolsAsync();

    private async void OnGoToDefinitionClick(object? sender, RoutedEventArgs e)
        => await EditorPane.Navigation.GoToDefinitionAsync();

    private async void OnFindReferencesClick(object? sender, RoutedEventArgs e)
        => await EditorPane.Navigation.FindReferencesAsync();

    private async void OnRenameSymbolClick(object? sender, RoutedEventArgs e)
        => await EditorPane.Navigation.RenameSymbolAsync();


    private async void OnFindInFilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await ViewModel.Search.SearchWorkspaceCommand.ExecuteAsync(true);
    }

    private async void OnReplaceWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await ViewModel.Search.ReplaceWorkspaceCommand.ExecuteAsync(true);
    }

    private void OnSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: WorkspaceSearchResult result })
            _messenger.Send(new NavigateToSearchResultMessage(result, ViewModel.Search.SearchQuery));
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    // ============================================================== status bar

    private void UpdateStatus()
    {
        var caret = EditorPane.EditorControl.TextArea.Caret;
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

    private void UpdateDebugToolbar(RunViewModel run)
    {
        if (run.IsDebugging)
        {
            if (_debugToolbar is not null)
                return;

            var toolbar = new DebugToolbarWindow(_debugToolbarPosition) { DataContext = run };
            toolbar.Closed += (_, _) =>
            {
                _debugToolbarPosition = toolbar.Position;   // reopen where the user left it
                if (ReferenceEquals(_debugToolbar, toolbar))
                    _debugToolbar = null;
            };
            _debugToolbar = toolbar;
            toolbar.Show(this);   // owned: minimises with the main window and closes with it
        }
        else
        {
            _debugToolbar?.Close();
            _debugToolbar = null;
        }
    }
}