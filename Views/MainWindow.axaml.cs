using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
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
using ICSharpCode.Decompiler;
using static AXaxmlIDE.Helpers.LspHelpers;
using AXaxmlIDE.EditorFeatures;

// ReSharper disable AsyncVoidEventHandlerMethod

namespace AXaxmlIDE.Views;

public partial class MainWindow : Window
{
    private readonly TextMate.Installation _textMate;
    private readonly CustomMargin? _customMargin;
    private readonly CodeActionMargin _codeActionMargin;
    private readonly RegistryOptions? _registryOptions;
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

    public MainWindow()
    {
        InitializeComponent();
        _ctx = new EditorContext(Editor, () => ViewModel);
        _dialogs = new DialogService(this);

        // TreeViewItem.Expanded bubbles, so one handler on the TreeView catches every node.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnNodeExpanded, RoutingStrategies.Bubble);
        FileTree.AddHandler(PointerPressedEvent, OnFileTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        _occurrences = new OccurrenceController(_ctx);
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateStatus();
        };

        Editor.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        //Editor.TextArea.RightClickMovesCaret = true;
        Editor.Options.EnableTextDragDrop = true;
        Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

        _customMargin = new CustomMargin();
        Editor.TextArea.LeftMargins.Insert(1, _codeActionMargin);
        _edits = new WorkspaceEditService(_ctx);
        _codeActions = new CodeActionController(_ctx, _edits);
        _hover = new HoverController(_ctx, _codeActions);
        _signature = new SignatureHelpController(_ctx);
        _completion = new CompletionController(_ctx, _hover);
        _typing = new TypingController(_ctx, _hover, _completion, _signature);

        var settingsStore = new EditorSettingsStore();
        var settingsLoad = settingsStore.Load();
        var settings = settingsLoad.Settings;
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _registryOptions = registryOptions;
        _textMate = Editor.InstallTextMate(registryOptions);
        _textMate.AppliedTheme += TextMateInstallationOnAppliedTheme;
        //_textMate.SetTheme(ThemeCatalog.Find(settings.Theme.ToString()).Load(registryOptions));
        var viewModel = new MainWindowViewModel(_textMate, registryOptions, settings, settingsStore, StorageProvider, this);
        _textMate.SetTheme(viewModel.SelectedTheme.Load(registryOptions));
        DataContext = viewModel;
        viewModel.EditorSettingsChanged += ApplyEditorSettings;
        viewModel.SolutionExplorerSideChanged += ApplySolutionExplorerSide;
        viewModel.StatusMessageChanged += viewModel.ShowStatusMessage;
        viewModel.DiagnosticsChanged += OnDiagnosticsChanged;
        viewModel.NavigateToProblemRequested += NavigateToProblem;
        viewModel.DebugLocationChanged += NavigateToDebugLocation;
        viewModel.WorkspaceEditRequested = _edits.ApplyServerEditAsync;
        ApplyEditorSettings();
        ApplySolutionExplorerSide(settings.SolutionExplorerOnRight);
        if (settingsLoad.Warnings.Count > 0)
            viewModel.ShowStatusMessage(string.Join(" ", settingsLoad.Warnings));


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
            _folding?.Dispose();
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
            HidePreview();
            Editor.Document = new TextDocument();
            Editor.IsReadOnly = false;
            _diagnosticRenderer.Segments = null;
            _ctx.Attach(null);
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
        _ctx.Attach(doc);
        _diagnosticRenderer.Segments = doc.Diagnostics;
        SetGrammarForFile(doc.Path);
        Editor.CaretOffset = Math.Min(doc.CaretOffset, doc.Document.TextLength);

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
        if (editorPoint is { X: >= 0, Y: >= 0 }
            && editorPoint.X < Editor.Bounds.Width && editorPoint.Y < Editor.Bounds.Height)
        {
            var nextSize = ViewModel.EditorFontSize + (e.Delta.Y > 0 ? 1 : -1);
            ViewModel.EditorFontSize = Math.Clamp(nextSize, 6, 72);
            e.Handled = true;
            return;
        }

        var previewPoint = e.GetPosition(PreviewViewport);
        if (!PreviewViewport.IsVisible
            || !(previewPoint.X >= 0) || !(previewPoint.Y >= 0)
            || !(previewPoint.X < PreviewViewport.Bounds.Width)
            || !(previewPoint.Y < PreviewViewport.Bounds.Height)) return;
        StepPreviewZoom(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
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
                _ = _completion.ShowCompletionAsync(doc, triggerChar: null);
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
            _ = _signature.RequestAsync(document, triggerCharacter: null);
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

            var selectedPath = await _dialogs.ShowQuickOpenDialogAsync(files);
            if (selectedPath is null)
                return;

            DocTabs.SelectedItem = ViewModel.OpenDocument(selectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.ShowStatusMessage($"Quick Open failed: {ex.Message}");
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

        var query = await _dialogs.PromptForTextAsync("Go to Symbol", "Search types and members...");
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

            var selected = await _dialogs.PickLocationAsync(
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
            : await _dialogs.PickLocationAsync(title, locations, FormatLocation);
        if (selected is not null)
            await NavigateToLocationAsync(selected);
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

        var newName = await _dialogs.PromptForTextAsync("Rename Symbol", "Enter the new symbol name...");
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

            if (!await _dialogs.ConfirmRenameAsync(replacements))
                return;

            _edits.Apply(replacements);
            ViewModel.ShowStatusMessage($"Applied rename changes in {replacements.Count} file(s). Review and save the modified documents.");
        }
        catch (Exception ex)
        {
            ViewModel.ShowStatusMessage($"Rename failed: {ex.Message}");
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
        if (!doc.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) return;
        _previewUpdateTimer.Stop();
        _previewUpdateTimer.Start();
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
