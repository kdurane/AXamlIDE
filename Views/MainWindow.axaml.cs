using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using AXaxmlIDE.CompletionEngine.Parsing;
using AXaxmlIDE.Enums;
using AXaxmlIDE.Lsp;
using AXaxmlIDE.Models;
using AXaxmlIDE.Services;
using AXaxmlIDE.ViewModels;
using TextMateSharp.Grammars;
using ICSharpCode.Decompiler;

// ReSharper disable AsyncVoidEventHandlerMethod

namespace AXaxmlIDE.Views;

public partial class MainWindow : Window
{
    private FoldingManager? _foldingManager;
    private FoldingCaretMargin? _foldingCaretMargin;
    private readonly TextMate.Installation _textMate;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;
    private readonly CustomMargin? _customMargin;
    private readonly CodeActionMargin _codeActionMargin;
    private readonly RegistryOptions? _registryOptions;
    private readonly OccurrenceRenderer _occurrenceRenderer = new();
    private readonly AvaloniaPreviewProcess _previewProcess = new();
    private readonly DispatcherTimer _previewUpdateTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _previewFitTimer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _outlineTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };
    private XamlPreviewTarget? _previewTarget;
    private static readonly double[] PreviewZoomLevels = [12.5, 25, 50, 75, 100, 150, 200, 400, 800];

    private readonly DiagnosticRenderer _diagnosticRenderer = new();
    private OpenDocument? _active;
    private TextDocument? _occurrenceDocument;
    private int _signatureHelpSequence;
    private CancellationTokenSource? _signatureHelpCts;

    private bool _treeOnRight;
    private bool _explorerVisible = true;

    // Hover: the pointer rests on an offset, a debounce timer fires, the server is asked.
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _hoverCts;
    private int _hoverOffset = -1;
    private (int Start, int End)? _tipRange; // non-null while our tooltip is open
    private bool _tipOpen;

    // Completion
    private int _completionSeq;

    public MainWindow()
    {
        InitializeComponent();

        // TreeViewItem.Expanded bubbles, so one handler on the TreeView catches every node.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerPressedEvent, OnFileTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_occurrenceRenderer);
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateStatus();
            //UpdateOutlineSelection();
            if (_insightWindow?.IsVisible == true && _active is { } doc)
                _ = RequestSignatureHelpAsync(doc, triggerCharacter: null, isRetrigger: true);
        };
        Editor.TextArea.SelectionChanged += (_, _) => UpdateOccurrenceHighlights();
        Editor.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        //Editor.TextArea.RightClickMovesCaret = true;
        Editor.Options.EnableTextDragDrop = true;
        Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

        // handledEventsToo: the text area marks pointer events handled while selecting.
        Editor.AddHandler(PointerMovedEvent, OnEditorPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

        _customMargin = new CustomMargin();
        Editor.TextArea.LeftMargins.Insert(0, _customMargin);
        _codeActionMargin = new CodeActionMargin(
            (line, anchor) => ShowCodeActionsAsync(line, anchor),
            () => _active?.Client?.SupportsCodeActions == true);
        Editor.TextArea.LeftMargins.Insert(1, _codeActionMargin);

        var settingsStore = new EditorSettingsStore();
        var settingsLoad = settingsStore.Load();
        var settings = settingsLoad.Settings;
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _registryOptions = registryOptions;
        _textMate = Editor.InstallTextMate(registryOptions);
        _textMate.AppliedTheme += TextMateInstallationOnAppliedTheme;
        //_textMate.SetTheme(ThemeCatalog.Find(settings.Theme.ToString()).Load(registryOptions));
        var viewModel = new MainWindowViewModel(_textMate, registryOptions, settings, settingsStore,StorageProvider,this);
        _textMate.SetTheme(viewModel.SelectedTheme.Load(registryOptions));
        DataContext = viewModel;
        viewModel.EditorSettingsChanged += ApplyEditorSettings;
        viewModel.SolutionExplorerSideChanged += ApplySolutionExplorerSide;
        viewModel.StatusMessageChanged += viewModel.ShowStatusMessage;
        viewModel.DiagnosticsChanged += OnDiagnosticsChanged;
        viewModel.NavigateToProblemRequested += NavigateToProblem;
        viewModel.DebugLocationChanged += NavigateToDebugLocation;
        viewModel.WorkspaceEditRequested = ApplyServerWorkspaceEditAsync;
        ApplyEditorSettings();
        ApplySolutionExplorerSide(settings.SolutionExplorerOnRight);
        if (settingsLoad.Warnings.Count > 0)
            viewModel.ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));

        Editor.PointerExited += (_, _) => HideTip(resetOffset: true);
        _hoverTimer.Tick += OnHoverTimer;
        _previewUpdateTimer.Tick += OnPreviewUpdateTimer;
        _previewFitTimer.Tick += OnPreviewFitTimer;
        _outlineTimer.Tick += OnOutlineTimer;
        _previewProcess.FrameReceived += OnPreviewFrameReceived;
        _previewProcess.ErrorChanged += OnPreviewErrorChanged;
        PreviewZoomComboBox.SelectionChanged += OnPreviewZoomSelectionChanged;
        PreviewThemeComboBox.SelectionChanged += OnPreviewThemeSelectionChanged;
        PreviewViewport.SizeChanged += OnPreviewViewportSizeChanged;
        viewModel.DocumentLanguageClientAttached += OnDocumentLanguageClientAttached;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        Closed += (_, _) =>
        {
            _previewUpdateTimer.Stop();
            _previewFitTimer.Stop();
            _outlineTimer.Stop();
            _previewProcess.Dispose();
            _customMargin?.SetDocument(null);
            viewModel.Dispose();
            _textMate.Dispose();
        };

        UpdateStatus();

        _foldingTimer.Tick += (_, _) =>
        {
            _foldingTimer.Stop();

            if (_active is { } doc)
                UpdateFolding(doc);
        };
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


    // private void UpdateOutlineSelection()
    // {
    //     if (_active is not { LanguageId: "csharp" })
    //         return;

    //     ViewModel.SelectDocumentOutlineItem(Editor.CaretOffset);
    // }


    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedDocumentOutlineItem is not { } item
            || item.StartOffset <= Editor.CaretOffset && Editor.CaretOffset <= item.EndOffset)
            return;

        Editor.CaretOffset = Math.Clamp(item.NavigationOffset, 0, Editor.Document.TextLength);
        Editor.Focus();

        Dispatcher.UIThread.Post(() =>
        {
            var caret = Editor.TextArea.Caret;
            Editor.ScrollTo(caret.Line, caret.Column);
        }, DispatcherPriority.Render);
    }

    // ================================================================= tabs

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        HideTip(resetOffset: true);
        _completionWindow?.Close();
        CloseSignatureInsight();

        if (_active is { } prev) // save view state of the tab we're leaving
        {
            prev.CaretOffset = Editor.CaretOffset;
            prev.VerticalOffset = Editor.VerticalOffset;
            prev.HorizontalOffset = Editor.HorizontalOffset;
        }

        // FoldingManager installs document-bound generators; detach them while
        // the old document is still attached to the editor.
        ConfigureFolding(null);
        _active = DocTabs.SelectedItem as OpenDocument;
        ViewModel.ActiveDocument = _active;
        _customMargin?.SetDocument(_active);
        _codeActionMargin.SetDocument(_active);
        if (_occurrenceDocument is not null)
        {
            _occurrenceDocument.Changed -= OnOccurrenceDocumentChanged;
            _occurrenceDocument.Changed -= OnActiveDocumentChanged;
        }
        _occurrenceDocument = _active?.Document;
        if (_occurrenceDocument is not null)
        {
            _occurrenceDocument.Changed += OnOccurrenceDocumentChanged;
            _occurrenceDocument.Changed += OnActiveDocumentChanged;
        }

        if (_active is null)
        {
            _outlineTimer.Stop();
            ViewModel.ClearDocumentOutline();
            HidePreview();
            Editor.Document = new TextDocument();
            Editor.IsReadOnly = false;
            _diagnosticRenderer.Segments = null;
            _occurrenceRenderer.Update(null, null);
            SetGrammarForFile("");
            UpdateStatus();
            return;
        }

        var doc = _active;
        ViewModel.ClearDocumentOutline();
        QueueOutlineUpdate();
        UpdatePreviewPaneVisibility(doc);
        Editor.Document = doc.Document; // TextMate re-binds on DocumentChanged
        Editor.IsReadOnly = doc.IsReadOnly;
        ConfigureFolding(doc);
        _diagnosticRenderer.Segments = doc.Diagnostics;
        SetGrammarForFile(doc.Path);
        Editor.CaretOffset = Math.Min(doc.CaretOffset, doc.Document.TextLength);
        UpdateOccurrenceHighlights();

        // Scrolling needs a layout pass first.
        Dispatcher.UIThread.Post(() =>
        {
            Editor.ScrollToVerticalOffset(doc.VerticalOffset);
            Editor.ScrollToHorizontalOffset(doc.HorizontalOffset);
        }, DispatcherPriority.Loaded);

        Editor.Focus();
        UpdateStatus();
        _ = UpdatePreviewAsync(doc);
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
        if (!visible)
            HidePreview();
    }

    private void HidePreview()
    {
        _previewUpdateTimer.Stop();
        _previewFitTimer.Stop();
        PreviewImage.Source = null;
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
        _previewProcess.Stop();
        _previewTarget = null;
        PreviewMessage.Text = "Open an Avalonia AXAML file to start previewing.";
        PreviewMessageHost.IsVisible = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        var editorPoint = e.GetPosition(Editor);
        if (editorPoint.X >= 0 && editorPoint.Y >= 0
            && editorPoint.X < Editor.Bounds.Width && editorPoint.Y < Editor.Bounds.Height)
        {
            var nextSize = ViewModel.EditorFontSize + (e.Delta.Y > 0 ? 1 : -1);
            ViewModel.EditorFontSize = Math.Clamp(nextSize, 6, 72);
            e.Handled = true;
            return;
        }

        var previewPoint = e.GetPosition(PreviewViewport);
        if (PreviewViewport.IsVisible
            && previewPoint.X >= 0 && previewPoint.Y >= 0
            && previewPoint.X < PreviewViewport.Bounds.Width
            && previewPoint.Y < PreviewViewport.Bounds.Height)
        {
            StepPreviewZoom(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }
    }

    private void StepPreviewZoom(int direction)
    {
        var currentZoom = _previewProcess.Scaling / GetPreviewRenderScaling();
        var currentIndex = Array.IndexOf(PreviewZoomLevels,
            PreviewZoomLevels.OrderBy(level => Math.Abs(level / 100 - currentZoom)).First());
        var nextIndex = Math.Clamp(currentIndex + direction, 0, PreviewZoomLevels.Length - 1);
        var text = $"{PreviewZoomLevels[nextIndex]:0.##}%";
        PreviewZoomComboBox.SelectedItem = PreviewZoomComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), text, StringComparison.Ordinal));
    }

    private void OnPreviewZoomSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsFitPreviewZoomSelected())
            QueuePreviewFit();
        else
            _ = UpdateSelectedPreviewZoomAsync();
    }

    private async Task UpdateSelectedPreviewZoomAsync()
    {
        var option = GetComboBoxSelection(PreviewZoomComboBox);
        double zoom;
        if (option is "Fit All" or "Fit to Width")
        {
            if (PreviewImage.Source is not Avalonia.Media.Imaging.WriteableBitmap bitmap
                || bitmap.Size.Width <= 0
                || bitmap.Size.Height <= 0
                || PreviewViewport.Bounds.Width <= 0
                || PreviewViewport.Bounds.Height <= 0)
                return;

            var baseWidth = bitmap.Size.Width / _previewProcess.Scaling;
            var baseHeight = bitmap.Size.Height / _previewProcess.Scaling;
            var widthScale = PreviewViewport.Bounds.Width / baseWidth;
            zoom = option == "Fit to Width"
                ? widthScale
                : Math.Min(widthScale, PreviewViewport.Bounds.Height / baseHeight);
            zoom = Math.Round(Math.Clamp(zoom, 0.125, 8), 2, MidpointRounding.ToEven);
        }
        else if (double.TryParse(option?.TrimEnd('%'),
                     System.Globalization.NumberStyles.Number,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var percentage)
                 && percentage is > 0 and <= 800)
        {
            zoom = percentage / 100;
        }
        else
        {
            return;
        }

        try
        {
            await _previewProcess.SetScalingAsync(zoom * GetPreviewRenderScaling());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] Could not change preview zoom: {ex}");
            PreviewMessage.Text = $"Could not change preview zoom: {ex.Message}";
            PreviewMessageHost.IsVisible = true;
        }
    }

    private void OnPreviewViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsFitPreviewZoomSelected())
            QueuePreviewFit();
    }

    private bool IsFitPreviewZoomSelected()
        => GetComboBoxSelection(PreviewZoomComboBox) is "Fit All" or "Fit to Width";

    private void QueuePreviewFit()
    {
        _previewFitTimer.Stop();
        _previewFitTimer.Start();
    }

    private void OnPreviewFitTimer(object? sender, EventArgs e)
    {
        _previewFitTimer.Stop();
        if (IsFitPreviewZoomSelected())
            _ = UpdateSelectedPreviewZoomAsync();
    }

    private void OnPreviewThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_active is { } document
            && document.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            _ = UpdatePreviewAsync(document);
    }

    private string GetPreviewXaml(string xaml)
    {
        var theme = GetComboBoxSelection(PreviewThemeComboBox);
        if (theme is not ("Light" or "Dark"))
            return xaml;

        XDocument document;
        try
        {
            document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return xaml;
        }

        if (document.Root is not { } root)
            return xaml;

        if (root.Name.LocalName is "Window" or "Application" or "ThemeVariantScope")
        {
            root.SetAttributeValue("RequestedThemeVariant", theme);
        }
        else
        {
            XNamespace avaloniaNamespace = "https://github.com/avaloniaui";
            var themeScope = new XElement(
                avaloniaNamespace + "ThemeVariantScope",
                new XAttribute("RequestedThemeVariant", theme),
                root);
            root.ReplaceWith(themeScope);
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string? GetComboBoxSelection(ComboBox comboBox)
        => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private double GetPreviewRenderScaling()
        => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private async Task UpdatePreviewAsync(OpenDocument document)
    {
        if (!ReferenceEquals(document, _active)
            || !document.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            return;

        PreviewMessage.Text = "Starting Avalonia preview...";
        PreviewMessageHost.IsVisible = PreviewImage.Source is null;

        try
        {
            var target = await ViewModel.GetXamlPreviewTargetAsync(document.Path);
            if (!ReferenceEquals(document, _active))
                return;

            if (!_previewProcess.IsRunning || target != _previewTarget)
            {
                PreviewImage.Source = null;
                PreviewImage.Width = double.NaN;
                PreviewImage.Height = double.NaN;
                _previewProcess.Stop();
                _previewTarget = target;
                PreviewMessage.Text = "Connecting to the Avalonia designer host...";
                PreviewMessageHost.IsVisible = true;
                await _previewProcess.StartAsync(target);
            }

            if (!ReferenceEquals(document, _active))
                return;

            await _previewProcess.UpdateXamlAsync(
                target.XamlAssemblyPath,
                document.Path,
                GetPreviewXaml(document.Document.Text));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] {ex}");
            if (ReferenceEquals(document, _active))
            {
                PreviewImage.Source = null;
                PreviewImage.Width = double.NaN;
                PreviewImage.Height = double.NaN;
                _previewProcess.Stop();
                _previewTarget = null;
                PreviewMessage.Text = $"Preview unavailable: {ex.Message}";
                PreviewMessageHost.IsVisible = true;
            }
        }
    }

    private void OnPreviewUpdateTimer(object? sender, EventArgs e)
    {
        _previewUpdateTimer.Stop();
        if (_active is { } document)
            _ = UpdatePreviewAsync(document);
    }

    private void OnPreviewFrameReceived(Avalonia.Media.Imaging.WriteableBitmap? bitmap)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PreviewImage.Source = bitmap;
            if (bitmap is null)
            {
                PreviewImage.Width = double.NaN;
                PreviewImage.Height = double.NaN;
            }
            else
            {
                var renderScaling = GetPreviewRenderScaling();
                PreviewImage.Width = bitmap.Size.Width / renderScaling;
                PreviewImage.Height = bitmap.Size.Height / renderScaling;
                if (IsFitPreviewZoomSelected())
                    QueuePreviewFit();
            }
            PreviewMessageHost.IsVisible = bitmap is null;
            if (bitmap is null && string.IsNullOrEmpty(PreviewMessage.Text))
                PreviewMessage.Text = "Waiting for preview frame...";
        });
    }

    private void OnPreviewErrorChanged(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            PreviewMessage.Text = message;
            PreviewMessageHost.IsVisible = true;
        });
    }

    private readonly DispatcherTimer _foldingTimer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

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
            switch (await AskSaveAsync(doc))
            {
                case SaveChoice.Cancel: return;
                case SaveChoice.Save: await ViewModel.SaveDocumentAsync(doc); break;
            }
        }

        ViewModel.CloseDocument(doc);

        if (ViewModel.Documents.Count > 0 && DocTabs.SelectedItem is null)
            DocTabs.SelectedIndex = Math.Min(index, ViewModel.Documents.Count - 1);
    }

    private async Task<SaveChoice> AskSaveAsync(OpenDocument doc)
    {
        var result = SaveChoice.Cancel; // closing the window with the X = cancel

        var dialog = new Window
        {
            Title = "Unsaved changes",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        dialog.Content = new StackPanel
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
        };

        await dialog.ShowDialog(this);
        return result;

        Button Make(string text, SaveChoice choice)
        {
            var b = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) =>
            {
                result = choice;
                dialog.Close();
            };
            return b;
        }
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
                _ = GoToDefinitionAsync();
                e.Handled = true;
                return;
            case { Key: Key.F12, KeyModifiers: KeyModifiers.Shift }:
                _ = FindReferencesAsync();
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
                        _ = RequestSignatureHelpAsync(activeDocument, triggerCharacter: null);
                    e.Handled = true;
                    return;
                }
            case { KeyModifiers: KeyModifiers.Control, Key: Key.OemPeriod }:
                _ = ShowCodeActionsAsync();
                e.Handled = true;
                return;
        }

        if (e.KeyModifiers != KeyModifiers.Control) return;

        switch (e.Key)
        {
            case Key.P:
                _ = QuickOpenAsync();
                e.Handled = true;
                break;
            case Key.T:
                _ = SearchWorkspaceSymbolsAsync();
                e.Handled = true;
                break;
            case Key.R:
                _ = RenameSymbolAsync();
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
                _ = ShowCompletionAsync(doc, triggerChar: null);
                e.Handled = true;
                break;
        }
    }

    // ============================================================== grammar

    private void SetGrammarForFile(string path)
    {
        var language = string.IsNullOrEmpty(path)
            ? null
            : _registryOptions?.GetLanguageByExtension(Path.GetExtension(path));

        if (language is null)
        {
            // Don't leave the previous file's grammar applied.
            // (If your AvaloniaEdit.TextMate version rejects null here, pass an unknown scope instead.)
            _textMate.SetGrammar(null);
            return;
        }

        _textMate.SetGrammar(_registryOptions?.GetScopeByLanguageId(language.Id));
    }

    // ================================================================== LSP

    private void OnSignatureHelpClick(object? sender, RoutedEventArgs e)
    {
        if (_active is { } document)
            _ = RequestSignatureHelpAsync(document, triggerCharacter: null);
    }

    private async void OnCodeActionsClick(object? sender, RoutedEventArgs e)
        => await ShowCodeActionsAsync(anchor: Editor);

    private async Task<bool> ShowCodeActionsAsync(
        int? requestedLine = null,
        Control? anchor = null,
        string? diagnosticHeader = null,
        bool showAtPointer = false)
    {
        if (_active is not { Client: { } client, Sync: { } sync } document)
        {
            ViewModel.ShowStatusMessage("Open a document with an active language server to request quick actions.");
            return false;
        }

        if (!client.SupportsCodeActions)
        {
            ViewModel.ShowStatusMessage("The active language server does not support quick actions.");
            return false;
        }

        try
        {
            sync.Flush();
            var startOffset = Editor.SelectionLength > 0
                ? Editor.SelectionStart
                : requestedLine is { } lineNumber
                    ? document.Document.GetLineByNumber(lineNumber).Offset
                    : document.Document.GetLineByOffset(Editor.CaretOffset).Offset;
            var endOffset = Editor.SelectionLength > 0
                ? Editor.SelectionStart + Editor.SelectionLength
                : requestedLine is { } requestedLineNumber
                    ? document.Document.GetLineByNumber(requestedLineNumber).EndOffset
                    : document.Document.GetLineByOffset(Editor.CaretOffset).EndOffset;
            var start = document.Document.GetLocation(startOffset);
            var end = document.Document.GetLocation(endOffset);
            var range = new LspRange(start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1);
            var diagnostics = document.Diagnostics.Select(segment =>
            {
                var diagnosticStart = document.Document.GetLocation(segment.StartOffset);
                var diagnosticEnd = document.Document.GetLocation(segment.EndOffset);
                return new LspDiagnostic(
                    diagnosticStart.Line - 1, diagnosticStart.Column - 1,
                    diagnosticEnd.Line - 1, diagnosticEnd.Column - 1,
                    segment.Severity, segment.Message, segment.RawDiagnostic);
            }).Where(diagnostic => RangesOverlap(
                range,
                new LspRange(
                    diagnostic.StartLine,
                    diagnostic.StartChar,
                    diagnostic.EndLine,
                    diagnostic.EndChar)))
                .ToArray();
            var actions = await client.CodeActionsAsync(document.Uri, range, diagnostics);
            if (!ReferenceEquals(document, _active))
                return false;
            if (actions.Count == 0)
            {
                ViewModel.ShowStatusMessage("No quick actions are available at the caret or selection.");
                return false;
            }

            var header = diagnosticHeader ?? string.Join(
                Environment.NewLine,
                diagnostics.Select(diagnostic => diagnostic.Message).Distinct(StringComparer.Ordinal));
            HideTip(resetOffset: true);
            ShowCodeActionFlyout(actions, client, anchor ?? Editor, header, showAtPointer);
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Quick action failed: {ex.Message}");
            return false;
        }
    }

    private static bool RangesOverlap(LspRange first, LspRange second)
    {
        var firstStart = (first.StartLine, first.StartChar);
        var firstEnd = (first.EndLine, first.EndChar);
        var secondStart = (second.StartLine, second.StartChar);
        var secondEnd = (second.EndLine, second.EndChar);
        return Compare(firstStart, secondEnd) <= 0 && Compare(secondStart, firstEnd) <= 0;

        static int Compare((int Line, int Character) a, (int Line, int Character) b)
            => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);
    }

    private void ShowCodeActionFlyout(IReadOnlyList<LspCodeAction> actions, LspClient client,
        Control anchor, string? diagnosticHeader, bool showAtPointer)
    {
        var flyout = new MenuFlyout();
        if (!string.IsNullOrWhiteSpace(diagnosticHeader))
        {
            flyout.Items.Add(new MenuItem
            {
                IsEnabled = false,
                FontSize = 10,
                Padding = new Thickness(8, 6),
                Header = new TextBlock
                {
                    Text = diagnosticHeader,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.Salmon,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360
                }
            });
            flyout.Items.Add(new Separator());
        }

        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                FontSize = 10,
                Padding = new Thickness(8, 4),
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new PathIcon
                        {
                            Data = Geometry.Parse(
                                "M12 2a7 7 0 0 0-4 12.75V17a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-2.25A7 7 0 0 0 12 2m2 13.5V17h-4v-1.5l-.8-.55a5 5 0 1 1 5.6 0zM10 21h4v1h-4z"),
                            Width = 14,
                            Height = 14,
                            Foreground = Brushes.DarkGoldenrod
                        },
                        new TextBlock
                        {
                            Text = action.Title,
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            item.Click += async (_, _) => await ApplyCodeActionAsync(action, client);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor, showAtPointer);
    }

    private async Task ApplyCodeActionAsync(LspCodeAction action, LspClient client)
    {
        try
        {
            var selected = client.SupportsCodeActionResolve
                ? await client.ResolveCodeActionAsync(action)
                : action;
            var didWork = false;
            if (selected.Edit is { } edit)
            {
                var replacements = ParseWorkspaceEdit(edit);
                if (replacements.Count > 0)
                {
                    ApplyWorkspaceEdits(replacements);
                    didWork = true;
                }
            }

            if (selected.Command is { } command)
            {
                if (command.ValueKind != JsonValueKind.Object
                    || !command.TryGetProperty("command", out var commandName)
                    || commandName.GetString() is not { Length: > 0 } name)
                    throw new InvalidOperationException($"Quick action '{selected.Title}' returned an invalid command.");

                var arguments = command.TryGetProperty("arguments", out var values)
                    ? values.Clone()
                    : (JsonElement?)null;
                await client.ExecuteCommandAsync(name, arguments);
                didWork = true;
            }

            ViewModel.ShowStatusMessage(didWork
                ? $"Applied '{selected.Title}'. Review and save modified documents."
                : $"'{selected.Title}' did not contain an applicable edit or command.");
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Quick action failed: {ex.Message}");
        }
    }

    private void OnProblemClick(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is not ProblemItem problem)
            return;

        NavigateToProblem(problem);
    }

    private void NavigateToProblem(ProblemItem problem)
    {
        DocTabs.SelectedItem = problem.Document;
        Editor.CaretOffset = Math.Min(problem.Offset, Editor.Document.TextLength);
        Editor.Focus();

        Dispatcher.UIThread.Post(() =>
        {
            var caret = Editor.TextArea.Caret;
            Editor.ScrollTo(caret.Line, caret.Column);
        }, DispatcherPriority.Render);
    }

    private void OnFindInFilesClick(object? sender, RoutedEventArgs e) => ShowFindInFiles();

    private void ShowFindInFiles()
    {
        ToolTabs.SelectedIndex = 4;
        FindInFilesQuery.Focus();
        FindInFilesQuery.SelectAll();
    }

    private async void OnWorkspaceSymbolsClick(object? sender, RoutedEventArgs e)
        => await SearchWorkspaceSymbolsAsync();

    private async void OnGoToDefinitionClick(object? sender, RoutedEventArgs e)
        => await GoToDefinitionAsync();

    private async void OnFindReferencesClick(object? sender, RoutedEventArgs e)
        => await FindReferencesAsync();

    private async void OnRenameSymbolClick(object? sender, RoutedEventArgs e)
        => await RenameSymbolAsync();

    private async Task QuickOpenAsync()
    {
        try
        {
            var files = await ViewModel.GetWorkspaceFilesAsync();
            if (files.Count == 0)
            {
                ViewModel.ShowStatusMessage("No workspace files are available to open.");
                return;
            }

            var selectedPath = await ShowQuickOpenDialogAsync(files);
            if (selectedPath is null)
                return;

            DocTabs.SelectedItem = ViewModel.OpenDocument(selectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.ShowStatusMessage($"Quick Open failed: {ex.Message}");
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
        await window.ShowDialog(this);
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

    private async Task SearchWorkspaceSymbolsAsync()
    {
        if (!TryGetActiveLanguageClient(out _, out var client))
            return;
        if (!client.SupportsWorkspaceSymbols)
        {
            ViewModel.ShowStatusMessage("The active language server does not support workspace symbols.");
            return;
        }

        var query = await PromptForTextAsync("Go to Symbol", "Search types and members...");
        if (string.IsNullOrWhiteSpace(query))
            return;

        try
        {
            var symbols = await client.WorkspaceSymbolsAsync(query);
            if (symbols.Count == 0)
            {
                ViewModel.ShowStatusMessage("No matching workspace symbols.");
                return;
            }

            var selected = await PickLocationAsync(
                "Workspace Symbols",
                symbols,
                symbol => string.IsNullOrEmpty(symbol.ContainerName)
                    ? symbol.Name ?? "(unnamed symbol)"
                    : $"{symbol.Name}  —  {symbol.ContainerName}");
            if (selected is not null)
                await NavigateToLocationAsync(selected);
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Workspace symbol search failed: {ex.Message}");
        }
    }

    private async Task GoToDefinitionAsync()
    {
        if (_active?.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true)
        {
            await GoToXamlDefinitionAsync(_active);
            return;
        }

        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsDefinition)
        {
            ViewModel.ShowStatusMessage("The active language server does not support Go to Definition.");
            return;
        }

        try
        {
            var (line, character) = CurrentLspPosition();
            var locations = await client.DefinitionAsync(document.Uri, line, character);
            await NavigateToLocationsAsync("Go to Definition", locations);
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Go to Definition failed: {ex.Message}");
        }
    }

    private async Task GoToXamlDefinitionAsync(OpenDocument document)
    {
        try
        {
            var source = await ViewModel.DecompileXamlTypeAsync(
                document.Path, document.Document.Text, Editor.CaretOffset);
            if (source is null)
            {
                ViewModel.ShowStatusMessage("No resolvable AXAML type at the caret.");
                return;
            }

            var decompiledDocument = ViewModel.OpenReadOnlyDocument(source.Path, source.Text, "csharp");
            DocTabs.SelectedItem = decompiledDocument;
            ViewModel.ShowStatusMessage($"Opened decompiled source for {source.TypeFullName} (read-only).");
        }
        catch (Exception ex) when (ex is DecompilerException or IOException or UnauthorizedAccessException
                                   or InvalidOperationException or ArgumentException
                                   or BadImageFormatException or NotSupportedException)
        {
            ViewModel.ShowStatusMessage($"AXAML type navigation failed: {ex.Message}");
        }
    }

    private async Task FindReferencesAsync()
    {
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsReferences)
        {
            ViewModel.ShowStatusMessage("The active language server does not support Find All References.");
            return;
        }

        try
        {
            var (line, character) = CurrentLspPosition();
            var locations = await client.ReferencesAsync(document.Uri, line, character);
            await NavigateToLocationsAsync("Find All References", locations);
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Find All References failed: {ex.Message}");
        }
    }

    private async Task NavigateToLocationsAsync(string title, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
        {
            ViewModel.ShowStatusMessage($"No results for {title}.");
            return;
        }

        var selected = locations.Count == 1
            ? locations[0]
            : await PickLocationAsync(title, locations, FormatLocation);
        if (selected is not null)
            await NavigateToLocationAsync(selected);
    }

    private async Task<LspLocation?> PickLocationAsync(
        string title,
        IReadOnlyList<LspLocation> locations,
        Func<LspLocation, string> display)
    {
        LspLocation? selected = null;
        var list = new ListBox
        {
            MinWidth = 560,
            MaxHeight = 420,
            ItemsSource = locations.Select(display).ToArray()
        };
        var window = new Window
        {
            Title = title,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Select a result to navigate." },
                    list
                }
            }
        };

        list.SelectedIndex = 0;
        list.DoubleTapped += (_, _) => Accept();
        list.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            Accept();
        };
        await window.ShowDialog(this);
        return selected;

        void Accept()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= locations.Count)
                return;
            selected = locations[list.SelectedIndex];
            window.Close();
        }
    }

    private static string FormatLocation(LspLocation location)
    {
        var path = Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : location.Uri;
        return $"{Path.GetFileName(path)}  (line {location.Range.StartLine + 1})  —  {path}";
    }

    private async Task NavigateToLocationAsync(LspLocation location)
    {
        if (!Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            ViewModel.ShowStatusMessage($"Cannot navigate to unsupported location '{location.Uri}'.");
            return;
        }

        try
        {
            DocTabs.SelectedItem = ViewModel.OpenDocument(uri.LocalPath);
            var document = (OpenDocument)DocTabs.SelectedItem!;
            var offset = document.OffsetOf(location.Range.StartLine, location.Range.StartChar);
            Editor.CaretOffset = offset;
            var endOffset = document.OffsetOf(location.Range.EndLine, location.Range.EndChar);
            Editor.Select(offset, Math.Max(0, endOffset - offset));
            Editor.TextArea.Caret.BringCaretToView();
            Editor.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.ShowStatusMessage($"Couldn't open location '{uri.LocalPath}': {ex.Message}");
        }
        await Task.CompletedTask;
    }

    private async Task RenameSymbolAsync()
    {
        if (!TryGetActiveLanguageClient(out var document, out var client))
            return;
        if (!client.SupportsRename)
        {
            ViewModel.ShowStatusMessage("The active language server does not support Rename Symbol.");
            return;
        }

        var newName = await PromptForTextAsync("Rename Symbol", "Enter the new symbol name...");
        if (string.IsNullOrWhiteSpace(newName))
            return;

        try
        {
            var (line, character) = CurrentLspPosition();
            var edit = await client.RenameAsync(document.Uri, line, character, newName);
            var replacements = ParseWorkspaceEdit(edit);
            if (replacements.Count == 0)
            {
                ViewModel.ShowStatusMessage("The language server returned no rename changes.");
                return;
            }

            if (!await ConfirmRenameAsync(replacements))
                return;

            ApplyWorkspaceEdits(replacements);
            ViewModel.ShowStatusMessage($"Applied rename changes in {replacements.Count} file(s). Review and save the modified documents.");
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Rename failed: {ex.Message}");
        }
    }

    private static Dictionary<string, List<(LspRange Range, string NewText)>> ParseWorkspaceEdit(JsonElement workspaceEdit)
    {
        var editsByPath = new Dictionary<string, List<(LspRange, string)>>(StringComparer.OrdinalIgnoreCase);

        void AddEdits(string uriText, JsonElement edits)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
                throw new InvalidOperationException($"Rename returned a non-file URI: {uriText}");
            if (edits.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Rename returned an invalid edit list.");

            var path = Path.GetFullPath(uri.LocalPath);
            if (!editsByPath.TryGetValue(path, out var pathEdits))
                editsByPath[path] = pathEdits = [];
            foreach (var edit in edits.EnumerateArray())
            {
                if (!edit.TryGetProperty("range", out var range)
                    || !edit.TryGetProperty("newText", out var newText))
                    throw new InvalidOperationException("Rename returned an unsupported text edit.");
                pathEdits.Add((ParseLspRange(range), newText.GetString() ?? ""));
            }
        }

        if (workspaceEdit.ValueKind != JsonValueKind.Object)
            return editsByPath;

        if (workspaceEdit.TryGetProperty("changes", out var changes)
            && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in changes.EnumerateObject())
                AddEdits(file.Name, file.Value);
        }

        if (workspaceEdit.TryGetProperty("documentChanges", out var documentChanges)
            && documentChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (!change.TryGetProperty("textDocument", out var textDocument)
                    || !textDocument.TryGetProperty("uri", out var uri)
                    || !change.TryGetProperty("edits", out var edits))
                    throw new InvalidOperationException("Rename returned an unsupported resource operation.");
                AddEdits(uri.GetString() ?? "", edits);
            }
        }

        return editsByPath;
    }

    private static LspRange ParseLspRange(JsonElement range)
    {
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        return new LspRange(
            start.GetProperty("line").GetInt32(),
            start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(),
            end.GetProperty("character").GetInt32());
    }

    private void ApplyWorkspaceEdits(Dictionary<string, List<(LspRange Range, string NewText)>> changes)
    {
        var prepared = new List<(OpenDocument Document, List<(int Start, int End, string Text)> Edits)>();
        foreach (var (path, textEdits) in changes)
        {
            var document = ViewModel.OpenDocument(path);
            if (document.IsReadOnly)
                throw new InvalidOperationException($"Cannot apply an edit to read-only document '{path}'.");
            var offsets = textEdits.Select(edit =>
            {
                var start = document.OffsetOf(edit.Range.StartLine, edit.Range.StartChar);
                var end = document.OffsetOf(edit.Range.EndLine, edit.Range.EndChar);
                return end < start ? throw new InvalidOperationException($"Rename returned an invalid range for '{path}'.") : (Start: start, End: end, Text: edit.NewText);
            }).OrderByDescending(edit => edit.Start).ToList();

            for (var index = 1; index < offsets.Count; index++)
                if (offsets[index - 1].Start < offsets[index].End)
                    throw new InvalidOperationException($"Rename returned overlapping edits for '{path}'.");

            prepared.Add((document, offsets));
        }

        foreach (var (document, edits) in prepared)
            foreach (var edit in edits)
                document.Document.Replace(edit.Start, edit.End - edit.Start, edit.Text);

        if (prepared.FirstOrDefault(item => ReferenceEquals(item.Document, _active)) is not
            { Document: { } activeDocument, Edits.Count: > 0 } activeEdit) return;
        {
            Editor.CaretOffset = Math.Clamp(
                activeEdit.Edits.Min(edit => edit.Start),
                0,
                activeDocument.Document.TextLength);
            Editor.TextArea.Caret.BringCaretToView();
            Editor.Focus();
        }
    }

    private Task<bool> ApplyServerWorkspaceEditAsync(JsonElement workspaceEdit)
        => Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var changes = ParseWorkspaceEdit(workspaceEdit);
                if (changes.Count == 0)
                    return false;
                ApplyWorkspaceEdits(changes);
                ViewModel.ShowStatusMessage("Applied language-server edits. Review and save modified documents.");
                return true;
            }
            catch (Exception ex)
            {
                ViewModel.ShowStatusMessage($"Couldn't apply language-server edits: {ex.Message}");
                return false;
            }
        }).GetTask();

    private async Task<bool> ConfirmRenameAsync(
        IReadOnlyDictionary<string, List<(LspRange Range, string NewText)>> changes)
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = "Confirm Rename",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"Apply language-server rename edits in {changes.Count} file(s)? Changed files will be opened and left unsaved for review.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        Make("Apply Rename", true),
                        Make("Cancel", false)
                    }
                }
            }
        };
        await dialog.ShowDialog(this);
        return confirmed;

        Button Make(string text, bool accept)
        {
            var button = new Button { Content = text, MinWidth = 90 };
            button.Click += (_, _) =>
            {
                confirmed = accept;
                dialog.Close();
            };
            return button;
        }
    }

    private async Task<string?> PromptForTextAsync(string title, string placeholder)
    {
        string? value = null;
        var textBox = new TextBox { PlaceholderText = placeholder, MinWidth = 360 };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        textBox.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            Accept();
        };
        var search = new Button { Content = "Search", MinWidth = 80 };
        search.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                textBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { search, cancel }
                }
            }
        };
        dialog.Opened += (_, _) => textBox.Focus();
        await dialog.ShowDialog(this);
        return value;

        void Accept()
        {
            value = textBox.Text;
            dialog.Close();
        }
    }

    private bool TryGetActiveLanguageClient(out OpenDocument document, out LspClient client)
    {
        if (_active is { Client: not null, Sync: not null } active)
        {
            active.Sync.Flush();
            document = active;
            client = active.Client;
            return true;
        }

        document = null!;
        client = null!;
        ViewModel.ShowStatusMessage("Open a supported source document and wait for its language server before using symbol navigation.");
        return false;
    }

    private (int Line, int Character) CurrentLspPosition()
    {
        var location = Editor.Document.GetLocation(Editor.CaretOffset);
        return (location.Line - 1, location.Column - 1);
    }

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
        if ((sender as Control)?.Tag is not WorkspaceSearchResult result)
            return;

        try
        {
            DocTabs.SelectedItem = ViewModel.OpenDocument(result.FilePath);
            if (Editor.Document.LineCount < result.LineNumber)
                return;

            var line = Editor.Document.GetLineByNumber(result.LineNumber);
            var column = Math.Clamp(result.Column, 0, line.Length);
            var offset = line.Offset + column;
            var matchLength = 0;
            if (!string.IsNullOrEmpty(ViewModel.SearchQuery)
                && column <= line.Length - ViewModel.SearchQuery.Length
                && string.Compare(
                    Editor.Document.Text,
                    offset,
                    ViewModel.SearchQuery,
                    0,
                    ViewModel.SearchQuery.Length,
                    StringComparison.OrdinalIgnoreCase) == 0)
                matchLength = ViewModel.SearchQuery.Length;

            Editor.Select(offset, matchLength);
            Editor.TextArea.Caret.BringCaretToView();
            Editor.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.ShowStatusMessage($"Couldn't open search result '{result.FilePath}': {ex.Message}");
        }
    }

    private void OnDiagnosticsChanged(OpenDocument document)
    {
        if (!ReferenceEquals(document, _active)) return;
        _codeActionMargin.SetDocument(document);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        UpdateStatus();
    }

    // ============================================================ hover tooltip

    // Pointer moves are cheap: just note where it is and restart the debounce. The server
    // is only asked once the pointer has rested for a moment.
    private void OnEditorPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_active is not { } doc || Editor.GetPositionFromPoint(e.GetPosition(Editor)) is not { } pos)
        {
            HideTip(resetOffset: true);
            return;
        }

        var offset = doc.Document.GetOffset(pos.Location);

        // Still over what the open tooltip is about: leave it alone.
        if (_tipRange is { } range && offset >= range.Start && offset <= range.End) return;
        if (offset == _hoverOffset) return;

        _hoverOffset = offset;
        HideTip();
        _hoverTimer.Start();
    }

    private async void OnHoverTimer(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_active is not { } doc || _hoverOffset < 0) return;

        var offset = _hoverOffset;

        // Diagnostics are local and instant; the server's hover text is added if it has any.
        var segments = doc.Diagnostics.FindSegmentsContaining(offset).ToList();
        var diagText = string.Join(Environment.NewLine, segments.Select(d => d.Message));
        (int Start, int End)? range = segments.Count > 0 ? (segments[0].StartOffset, segments[0].EndOffset) : null;

        if (segments.Count > 0 && doc.Client?.SupportsCodeActions == true)
        {
            var diagnosticHeader = string.Join(
                Environment.NewLine,
                segments.Select(segment => segment.Message).Distinct(StringComparer.Ordinal));
            var lineNumber = doc.Document.GetLineByOffset(offset).LineNumber;
            if (await ShowCodeActionsAsync(lineNumber, Editor, diagnosticHeader, showAtPointer: true))
                return;
            if (!ReferenceEquals(doc, _active) || offset != _hoverOffset)
                return;
        }

        string? hoverText = null;
        var hoverIsMarkdown = false;

        if (doc.LanguageId == "axaml")
        {
            var cts = _hoverCts = new CancellationTokenSource();
            try
            {
                hoverText = await ViewModel.GetXamlHoverAsync(doc.Path, doc.Document.Text, offset);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AXAML hover] {ex}");
            }

            if (cts.IsCancellationRequested || offset != _hoverOffset || !ReferenceEquals(doc, _active))
                return;
        }
        else if (doc is { Client: { } client, Sync: { } sync })
        {
            sync.Flush(); // the server has to see what's on screen

            var cts = _hoverCts = new CancellationTokenSource();
            var loc = doc.Document.GetLocation(offset);

            try
            {
                var hover = await client.HoverAsync(doc.Uri, loc.Line - 1, loc.Column - 1, cts.Token);
                if (hover is not null)
                {
                    hoverText = hover.Text;
                    hoverIsMarkdown = hover.IsMarkdown;
                    if (hover.Range is { } r)
                        range = (doc.OffsetOf(r.StartLine, r.StartChar), doc.OffsetOf(r.EndLine, r.EndChar));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{doc.LanguageId} hover] {ex}");
            }

            // The pointer moved on, or the tab changed, while we were waiting.
            if (cts.IsCancellationRequested || offset != _hoverOffset || !ReferenceEquals(doc, _active))
                return;
        }

        ShowTip(diagText, hoverText, hoverIsMarkdown, range ?? (offset, offset));
    }

    private void ShowTip(string diagnostics, string? hover, bool hoverIsMarkdown, (int Start, int End) range)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };

        if (diagnostics.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = diagnostics,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Salmon
            });

        if (!string.IsNullOrEmpty(hover))
        {
            Control hoverContent;
            if (_active?.LanguageId == "axaml")
            {
                hoverContent = new TextBlock
                {
                    Text = hoverIsMarkdown ? MarkdownContentRenderer.ToPlainText(hover) : hover,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                    Foreground = Brushes.Gainsboro
                };
            }
            else if (hoverIsMarkdown)
            {
                var renderedMarkdown = MarkdownContentRenderer.Render(hover);
                if (renderedMarkdown is Panel { Children.Count: 0 })
                {
                    Debug.WriteLine($"[{_active?.LanguageId} hover] Markdown contained no renderable blocks.");
                    hoverContent = new TextBlock
                    {
                        Text = hover,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gainsboro
                    };
                }
                else
                    hoverContent = renderedMarkdown;
            }
            else
                hoverContent = new TextBlock
                {
                    Text = hover,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gainsboro
                };

            panel.Children.Add(hoverContent);
        }

        if (panel.Children.Count == 0) return;

        var background = new SolidColorBrush(Color.Parse("#2b2b2b"));
        var tooltipContent = new Border
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                MaxWidth = 536,
                MaxHeight = 420,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = panel
            }
        };

        ToolTip.SetTip(Editor, tooltipContent);
        ToolTip.SetPlacement(Editor, PlacementMode.Pointer);
        ToolTip.SetIsOpen(Editor, true);

        _tipOpen = true;
        _tipRange = range;
    }

    private void HideTip(bool resetOffset = false)
    {
        _hoverTimer.Stop();
        _hoverCts?.Cancel(); // abandons an in-flight hover request (and tells the server)
        if (resetOffset) _hoverOffset = -1;

        if (!_tipOpen) return;

        ToolTip.SetIsOpen(Editor, false);
        ToolTip.SetTip(Editor, null);
        _tipOpen = false;
        _tipRange = null;
    }

    // ============================================================== completion

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    // Characters that are part of an AXAML name (Grid.Row, x:Name, local-thing).
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '-';

    private static int WordStart(TextDocument doc, int offset)
    {
        while (offset > 0 && IsNameChar(doc.GetCharAt(offset - 1))) offset--;
        return offset;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private void OnOccurrenceDocumentChanged(object? sender, DocumentChangeEventArgs e)
        => UpdateOccurrenceHighlights();

    private void UpdateOccurrenceHighlights()
    {
        var selection = Editor.TextArea.Selection;
        var selectedText = selection.IsEmpty
            ? null
            : Editor.SelectedText;

        _occurrenceRenderer.Update(_active?.Document, selectedText);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // Typing something that isn't part of a name (=, space, >, ...) commits the selected item.
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow is { } window && !string.IsNullOrEmpty(e.Text) && !IsNameChar(e.Text[0]))
            window.CompletionList.RequestInsertion(e);
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        HideTip(resetOffset: true);

        if (e.Text == ")")
        {
            CloseSignatureInsight();
            return;
        }

        if (_active is not { } doc || string.IsNullOrEmpty(e.Text))
            return;

        if (doc.LanguageId == "axaml" && e.Text == ">")
            TryInsertXamlClosingTag(doc.Document);
        else if (doc.LanguageId == "csharp" && e.Text == "{")
            TryInsertClosingBrace(doc.Document);

        var client = doc.Client;
        var xamlTrigger = doc.LanguageId == "axaml"
            && (IsNameStart(e.Text[0]) || e.Text is "<" or " " or "." or "=" or "{" or ":" or ",");

        if (doc.LanguageId == "axaml" && _completionWindow is null
            && (xamlTrigger || client?.CompletionTriggerCharacters.Contains(e.Text) == true))
            _ = ShowCompletionAsync(doc, triggerChar: e.Text);
        else if (client is not null && doc.Sync is not null
                 && client.CompletionTriggerCharacters.Contains(e.Text))
            _ = ShowCompletionAsync(doc, triggerChar: e.Text);
        else if (client is not null && doc.Sync is not null
                 && _completionWindow is null && IsNameStart(e.Text[0]))
            _ = ShowCompletionAsync(doc, triggerChar: null);

        if (client is null || doc.Sync is null)
            return;

        if (client.SignatureHelpTriggerCharacters.Contains(e.Text))
        {
            _ = RequestSignatureHelpAsync(doc, e.Text);
        }
        else if (client.SignatureHelpRetriggerCharacters.Contains(e.Text))
        {
            _ = RequestSignatureHelpAsync(doc, e.Text, isRetrigger: true);
        }
        else if (e.Text == "(")
        {
            _ = RequestSignatureHelpAsync(doc, triggerCharacter: null);
        }
        else if (e.Text == "," && _insightWindow?.IsVisible == true)
        {
            _ = RequestSignatureHelpAsync(doc, e.Text, isRetrigger: true);
        }
        else if (_insightWindow?.IsVisible == true)
        {
            _ = RequestSignatureHelpAsync(doc, triggerCharacter: null, isRetrigger: true);
        }
    }

    private void TryInsertXamlClosingTag(TextDocument document)
    {
        var caret = Editor.CaretOffset;
        if (caret < 2 || document.GetCharAt(caret - 1) != '>')
            return;

        var textBeforeDelimiter = document.Text.AsMemory(0, caret - 1);
        var parser = XmlParser.Parse(textBeforeDelimiter);
        if (parser.IsInClosingTag
            || parser.TagName is not { Length: > 0 } tagName
            || parser.State is not (XmlParser.ParserState.StartElement
                or XmlParser.ParserState.InsideElement
                or XmlParser.ParserState.StartAttribute
                or XmlParser.ParserState.BeforeAttributeValue
                or XmlParser.ParserState.AttributeValue
                or XmlParser.ParserState.AfterAttributeValue))
            return;

        var charBeforeEnd = caret - 2;
        while (charBeforeEnd >= 0 && char.IsWhiteSpace(document.GetCharAt(charBeforeEnd)))
            charBeforeEnd--;
        if (charBeforeEnd >= 0 && document.GetCharAt(charBeforeEnd) == '/')
            return;

        var closingTag = $"</{tagName}>";
        var afterEnd = caret;
        while (afterEnd < document.TextLength && char.IsWhiteSpace(document.GetCharAt(afterEnd)))
            afterEnd++;
        if (document.Text.AsSpan(afterEnd).StartsWith(closingTag, StringComparison.Ordinal))
            return;

        document.Insert(caret, closingTag);
        Editor.CaretOffset = caret;
    }

    private void TryInsertClosingBrace(TextDocument document)
    {
        var caret = Editor.CaretOffset;
        if (caret < document.TextLength && document.GetCharAt(caret) == '}')
            return;

        document.Insert(caret, "}");
        Editor.CaretOffset = caret;
    }

    private async Task ShowCompletionAsync(OpenDocument doc, string? triggerChar)
    {
        var seq = ++_completionSeq;
        var caret = Editor.CaretOffset;

        if (doc.LanguageId == "axaml")
        {
            var requestedText = doc.Document.Text;
            try
            {
                var result = await ViewModel.GetXamlCompletionsAsync(doc.Path, requestedText, caret);
                if (result is { Completions.Count: > 0 }
                    && seq == _completionSeq
                    && ReferenceEquals(doc, _active)
                    && Editor.CaretOffset == caret
                    && doc.Document.Text == requestedText)
                {
                    var engineStart = Math.Clamp((int)result.StartPosition, 0, caret);
                    var engineWindow = new CompletionWindow(Editor.TextArea) { StartOffset = engineStart };
                    foreach (var completion in result.Completions)
                    {
                        Action? retrigger = completion.TriggerCompletionAfterInsert
                            ? () => Dispatcher.UIThread.Post(() =>
                            {
                                if (ReferenceEquals(doc, _active))
                                    _ = ShowCompletionAsync(doc, triggerChar: null);
                            })
                            : null;
                        engineWindow.CompletionList.CompletionData.Add(
                            new XamlEngineCompletionData(completion, retrigger));
                    }

                    ShowCompletionWindow(engineWindow, doc.Document, engineStart, caret);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AXAML completion] {ex}");
                ViewModel.ShowStatusMessage($"AXAML completion unavailable: {ex.Message}");
            }
        }

        if (doc.Client is not { } client || doc.Sync is null) return;

        doc.Sync.Flush(); // the server has to see what's on screen
        var loc = doc.Document.GetLocation(caret);
        IReadOnlyList<LspCompletionItem> items;
        try
        {
            items = await client.CompletionAsync(doc.Uri, loc.Line - 1, loc.Column - 1, triggerChar);
        }
        catch (Exception)
        {
            return; // server error or exited: just no popup
        }

        // Nothing to show, a newer request superseded this one, the tab changed, or the caret went backwards.
        if (items.Count == 0 || seq != _completionSeq
                             || !ReferenceEquals(doc, _active) || Editor.CaretOffset < caret)
            return;

        // The server knows where the word being completed starts; otherwise walk back over name characters.
        var start = items[0].Edit is { } first
            ? doc.OffsetOf(first.Range.StartLine, first.Range.StartChar)
            : WordStart(doc.Document, caret);
        start = Math.Min(start, caret);

        var window = new CompletionWindow(Editor.TextArea) { StartOffset = start };

        var rank = 0;
        foreach (var item in items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal))
        {
            var editStart = item.Edit is { } edit
                ? doc.OffsetOf(edit.Range.StartLine, edit.Range.StartChar)
                : start;
            window.CompletionList.CompletionData.Add(new LspCompletionData(item, editStart, priority: -rank++));
        }

        ShowCompletionWindow(window, doc.Document, start, caret);
    }

    private void ShowCompletionWindow(CompletionWindow window, TextDocument document, int start, int caret)
    {
        _completionWindow?.Close();
        HideTip(resetOffset: true);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window)) _completionWindow = null;
        };

        _completionWindow = window;
        window.Show();

        // Anything already typed after the word start becomes the initial filter.
        if (caret > start)
            window.CompletionList.SelectItem(document.GetText(start, caret - start));
    }

    private async Task RequestSignatureHelpAsync(OpenDocument doc, string? triggerCharacter, bool isRetrigger = false)
    {
        if (!ReferenceEquals(doc, _active) || doc.Client is not { } client || doc.Sync is null)
            return;

        doc.Sync.Flush();
        var requestId = ++_signatureHelpSequence;
        _signatureHelpCts?.Cancel();
        var cancellation = _signatureHelpCts = new CancellationTokenSource();
        var location = doc.Document.GetLocation(Editor.CaretOffset);

        try
        {
            var help = await client.SignatureHelpAsync(
                doc.Uri,
                location.Line - 1,
                location.Column - 1,
                triggerCharacter,
                isRetrigger,
                cancellation.Token);

            if (cancellation.IsCancellationRequested
                || requestId != _signatureHelpSequence
                || !ReferenceEquals(doc, _active))
                return;

            if (help is null)
            {
                CloseSignatureInsight();
                return;
            }

            var items = help.Signatures.Select(signature =>
            {
                var activeParameter = help.ActiveParameter ?? signature.ActiveParameter;
                var contentParts = new List<string>();
                if (signature.Documentation is { Length: > 0 } documentation)
                    contentParts.Add(documentation);

                if (activeParameter is not ({ } parameterIndex and >= 0)
                    || parameterIndex >= signature.Parameters.Count)
                    return new SignatureInsightItem(
                        signature.Label,
                        string.Join(Environment.NewLine + Environment.NewLine, contentParts));
                var parameter = signature.Parameters[parameterIndex];
                var parameterText = $"Active parameter: {parameter.Label}";
                if (!string.IsNullOrWhiteSpace(parameter.Documentation))
                    parameterText += Environment.NewLine + parameter.Documentation;
                contentParts.Add(parameterText);

                return new SignatureInsightItem(
                    signature.Label,
                    string.Join(Environment.NewLine + Environment.NewLine, contentParts));
            }).ToArray();

            ShowSignatureInsight(items, help.ActiveSignature);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Signature help failed: {ex}");
            if (requestId == _signatureHelpSequence)
                CloseSignatureInsight();
        }
        finally
        {
            if (ReferenceEquals(_signatureHelpCts, cancellation))
                _signatureHelpCts = null;
            cancellation.Dispose();
        }
    }

    // ============================================================== status bar

    private void UpdateStatus()
    {
        var caret = Editor.TextArea.Caret;
        ViewModel.ActiveCaretOffset = caret.Offset;
        CaretStatus.Text = _active is null ? "" : $"Ln {caret.Line}, Col {caret.Column}";
        DiagStatus.Text = _active is null ? "" : $"{_active.ErrorCount} errors, {_active.WarningCount} warnings";
    }

    private void ConfigureFolding(OpenDocument? doc)
    {
        if (_foldingCaretMargin is not null)
        {
            Editor.TextArea.LeftMargins.Remove(_foldingCaretMargin);
            _foldingCaretMargin = null;
        }

        // Remove folding from the previously active document.
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }

        if (doc is null || !IsFoldableFile(doc.Path))
            return;

        _foldingManager = FoldingManager.Install(Editor.TextArea);
        var defaultMargin = Editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
        if (defaultMargin is not null)
            Editor.TextArea.LeftMargins.Remove(defaultMargin);
        _foldingCaretMargin = new FoldingCaretMargin(_foldingManager);
        Editor.TextArea.LeftMargins.Add(_foldingCaretMargin);

        UpdateFolding(doc);
    }

    private void UpdateFolding(OpenDocument doc)
    {
        if (!ReferenceEquals(doc, _active) || _foldingManager is null)
            return;

        if (doc.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            new XmlFoldingStrategy().UpdateFoldings(_foldingManager, doc.Document);
        }
        else
        {
            _foldingManager.UpdateFoldings(CSharpFoldingStrategy.CreateFoldings(doc.Document), -1);
        }

        _foldingCaretMargin?.InvalidateVisual();
    }

    private static bool IsFoldableFile(string path)
        => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private void OnActiveDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_active is not { } doc) return;
        _foldingTimer.Stop();
        _foldingTimer.Start();
        if (doc.LanguageId == "csharp")
            QueueOutlineUpdate();
        if (!doc.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) return;
        _previewUpdateTimer.Stop();
        _previewUpdateTimer.Start();
    }

    private void ShowSignatureInsight(IReadOnlyList<SignatureInsightItem> items, int activeSignature = 0)
    {
        if (items.Count == 0)
        {
            CloseSignatureInsight();
            return;
        }

        if (_insightWindow is null)
        {
            _insightWindow = new OverloadInsightWindow(Editor.TextArea);
            _insightWindow.Closed += (_, _) => _insightWindow = null;
        }

        _insightWindow.Provider = new SignatureOverloadProvider(
            items,
            Math.Clamp(activeSignature, 0, items.Count - 1));

        if (!_insightWindow.IsVisible)
            _insightWindow.Show();
    }

    private void CloseSignatureInsight()
    {
        _signatureHelpSequence++;
        _signatureHelpCts?.Cancel();
        _insightWindow?.Close();
        _insightWindow = null;
    }

    private void TextMateInstallationOnAppliedTheme(object? sender, TextMate.Installation e)
    {
        ApplyThemeColorsToEditor(e);
        ApplyThemeColorsToWindow(e);
    }

    private void ApplyThemeColorsToEditor(TextMate.Installation e)
    {
        ApplyBrushAction(e, "editor.background", brush => Editor.Background = brush);
        ApplyBrushAction(e, "editor.foreground", brush => Editor.Foreground = brush);

        if (!ApplyBrushAction(e, "editor.selectionBackground",
                brush => Editor.TextArea.SelectionBrush = brush))
        {
            if (Application.Current!.TryGetResource("TextAreaSelectionBrush", out var resourceObject))
            {
                if (resourceObject is IBrush brush)
                {
                    Editor.TextArea.SelectionBrush = brush;
                }
            }
        }

        if (!ApplyBrushAction(e, "editor.lineHighlightBackground",
                brush =>
                {
                    Editor.TextArea.TextView.CurrentLineBackground = brush;
                    Editor.TextArea.TextView.CurrentLineBorder = new Pen(brush);
                    // Todo: VS Code didn't seem to have a border but it might be nice to have
                    // that option. For now just make it the same..
                }))
        {
            Editor.TextArea.TextView.SetDefaultHighlightLineColors();
        }

        //Todo: looks like the margin doesn't have a active line highlight, would be a nice addition
        if (!ApplyBrushAction(e, "editorLineNumber.foreground",
                brush => Editor.LineNumbersForeground = brush))
        {
            Editor.LineNumbersForeground = Editor.Foreground;
        }
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

        if (!ApplyBrushAction(e, "sideBar.background", brush => _customMargin?.BackGroundBrush = brush))
        {
            _customMargin?.SetDefaultBackgroundBrush();
        }

        //Applying the Editor background to the whole window for demo sake.
        ApplyBrushAction(e, "editor.background", brush => Background = brush);
        ApplyBrushAction(e, "editor.foreground", brush => Foreground = brush);
    }

    private bool ApplyBrushAction(TextMate.Installation e, string colorKeyNameFromJson,
        Action<IBrush> applyColorAction)
    {
        if (!e.TryGetThemeColor(colorKeyNameFromJson, out var colorString))
            return false;

        if (!Color.TryParse(colorString, out Color color))
            return false;

        var colorBrush = new SolidColorBrush(color);
        applyColorAction(colorBrush);
        return true;
    }
}
