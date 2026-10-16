using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using AXamlIDE.EditorFeatures;
using AXamlIDE.Lsp;
using AXamlIDE.Models;
using AXamlIDE.Services;
using AXamlIDE.ViewModels;
using TextMateSharp.Grammars;

namespace AXamlIDE.Views
{
    /// <summary>
    /// The editor, as a pane. Owns the TextEditor, its margins and renderers, TextMate, and every
    /// controller in EditorFeatures. MainWindow's only inputs are Initialize() once and
    /// SetDocument() on every tab change; its only outputs are StatusChanged and ThemeApplied.
    ///
    /// Everything in Initialize() is the old MainWindow constructor code, in the same order.
    /// SetDocument() is the editor half of the old OnTabSelectionChanged.
    /// </summary>
    public partial class EditorPaneView : UserControl
    {
        private EditorContext _ctx = null!;
        private readonly DiagnosticRenderer _diagnosticRenderer = new();
        private CustomMargin? _customMargin;
        private TextMateController _textMateController = null!;
        private FoldingController _folding = null!;
        private OccurrenceController _occurrences = null!;
        private TypingController _typing = null!;
        private HoverController _hover = null!;

        public EditorPaneView()
        {
            InitializeComponent();
        }

        // ---------------------------------------------------------------- outputs

        /// <summary>Caret moved or diagnostics changed: MainWindow refreshes the status bar.</summary>
        public event Action? StatusChanged;

        /// <summary>Editor colours are applied: MainWindow colours the status bar and window.</summary>
        public event Action<TextMate.Installation>? ThemeApplied;

        // ------------------------------------------------- what MainWindow can reach

        public TextEditor EditorControl => Editor;
        public EditorContext Context => _ctx;
        public TextMate.Installation TextMateInstallation { get; private set; } = null!;

        public EditorNavigator Navigator { get; private set; } = null!;
        public NavigationCommands Navigation { get; private set; } = null!;
        public WorkspaceEditService WorkspaceEdits { get; private set; } = null!;
        public CodeActionController CodeActions { get; private set; } = null!;
        public SignatureHelpController SignatureHelp { get; private set; } = null!;
        public CompletionController Completion { get; private set; } = null!;

        // ------------------------------------------------------------- initialise

        /// <summary>
        /// Call once from the MainWindow constructor, before the view model is created
        /// (the view model needs <see cref="TextMateInstallation"/>).
        /// </summary>
        public void Initialize(
            RegistryOptions registryOptions,
            Func<MainWindowViewModel> viewModel,
            DialogService dialogs,
            Action<OpenDocument> activateDocument,
            XamlLanguageService xaml,
            SettingsViewModel settings)
        {
            _ctx = new EditorContext(Editor, viewModel, xaml, settings) { ActivateDocument = activateDocument };

            Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
            _occurrences = new OccurrenceController(_ctx);

            Editor.TextArea.Caret.PositionChanged += (_, _) => StatusChanged?.Invoke();

            Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;

            //Editor.TextArea.RightClickMovesCaret = true;
            Editor.Options.EnableTextDragDrop = true;
            Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

            // Ctrl+wheel over the editor changes the font size. (The preview handles its own wheel.)
            Editor.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

            _customMargin = new CustomMargin();
            Editor.TextArea.LeftMargins.Insert(0, _customMargin);

            WorkspaceEdits = new WorkspaceEditService(_ctx);
            CodeActions = new CodeActionController(_ctx, WorkspaceEdits); // inserts its margin at index 1
            _hover = new HoverController(_ctx, CodeActions);
            SignatureHelp = new SignatureHelpController(_ctx);
            Completion = new CompletionController(_ctx, _hover);
            _typing = new TypingController(_ctx, _hover, Completion, SignatureHelp);
            Navigator = new EditorNavigator(_ctx);
            Navigation = new NavigationCommands(_ctx, dialogs, WorkspaceEdits, Navigator);
            _folding = new FoldingController(_ctx);

            TextMateInstallation = Editor.InstallTextMate(registryOptions);
            _textMateController = new TextMateController(_ctx, TextMateInstallation, registryOptions, _customMargin);
            _textMateController.ThemeApplied += e => ThemeApplied?.Invoke(e);
        }

        public void Shutdown()
        {
            _customMargin?.SetDocument(null);
            _folding.Dispose();
        }

        // ------------------------------------------------------------ the input

        /// <summary>
        /// The one input. Call on every tab change (null = no tab open). This is the editor half
        /// of the old OnTabSelectionChanged, in the same order.
        /// </summary>
        public void SetDocument(OpenDocument? doc)
        {
            _hover.Hide(resetOffset: true);

            if (_ctx.Active is { } prev) // save view state of the tab we're leaving
            {
                prev.CaretOffset = Editor.CaretOffset;
                prev.VerticalOffset = Editor.VerticalOffset;
                prev.HorizontalOffset = Editor.HorizontalOffset;
            }

            // FoldingManager installs document-bound generators; detach them while
            // the old document is still attached to the editor.
            _ctx.Detach();

            _customMargin?.SetDocument(doc);

            if (doc is null)
            {
                Editor.Document = new TextDocument();
                Editor.IsReadOnly = false;
                _diagnosticRenderer.Segments = null;
                _ctx.Attach(null);
                _textMateController.SetGrammarForFile("");
                return;
            }

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
        }

        // ------------------------------------------------------ view-model events

        public void ApplyEditorSettings()
        {
            var settings = _ctx.Settings;
            Editor.FontFamily = new FontFamily(settings.EditorFontFamily);
            Editor.FontSize = settings.EditorFontSize;
            Editor.ShowLineNumbers = settings.ShowLineNumbers;
            Editor.Options.HighlightCurrentLine = settings.HighlightCurrentLine;
        }

        /// <summary>Hook for MainWindowViewModel.DiagnosticsChanged.</summary>
        public void OnDiagnosticsChanged(OpenDocument document)
        {
            if (!ReferenceEquals(document, _ctx.Active)) return;
            CodeActions.RefreshMargin(document);
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            StatusChanged?.Invoke();
        }

        // -------------------------------------------------------------- handlers

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.Control)
                return;

            var settings = _ctx.Settings;
            var nextSize = settings.EditorFontSize + (e.Delta.Y > 0 ? 1 : -1);
            settings.EditorFontSize = Math.Clamp(nextSize, 6, 72);
            e.Handled = true;
        }

        // Context-menu "Quick Actions..." (the View menu has its own handler in MainWindow).
        private async void OnCodeActionsClick(object? sender, RoutedEventArgs e)
            => await CodeActions.ShowCodeActionsAsync(anchor: Editor);

        // Clipboard commands for the context menu. These were CopyMouseCommand etc. on
        // MainWindowViewModel, taking the TextArea (a view type) as a parameter.
        private void OnCopyClick(object? sender, RoutedEventArgs e)
            => ApplicationCommands.Copy.Execute(null, Editor.TextArea);

        private void OnCutClick(object? sender, RoutedEventArgs e)
            => ApplicationCommands.Cut.Execute(null, Editor.TextArea);

        private void OnPasteClick(object? sender, RoutedEventArgs e)
            => ApplicationCommands.Paste.Execute(null, Editor.TextArea);

        private void OnSelectAllClick(object? sender, RoutedEventArgs e)
            => ApplicationCommands.SelectAll.Execute(null, Editor.TextArea);
    }
}
