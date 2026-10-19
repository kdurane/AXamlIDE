using System;
using System.ComponentModel;
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
using AXamlIDE.Editor;
using AXamlIDE.Editor.CodeActions;
using AXamlIDE.Editor.CodeLens;
using AXamlIDE.Editor.CodeCompletion;
using AXamlIDE.Editor.Debugging;
using AXamlIDE.Editor.Folding;
using AXamlIDE.Editor.Highlighting;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Editor.Navigation;
using AXamlIDE.Editor.SignatureHelp;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.Messaging;
using TextMateSharp.Grammars;

namespace AXamlIDE.Views;

/// <summary>
/// The editor, as a pane. Owns the TextEditor, its margins and renderers, TextMate, and every
/// editor feature controller (completion, hover, folding, navigation...).
///
/// There is one per open document (each tab owns its editor, so a tab can be dragged to another
/// window with its editor state). Several can be visible at once, in different windows, so:
///   - editor actions (EditorActionMessage) only run in the pane showing DocumentManager.Active;
///   - navigation is asked of the document (OpenDocument.Reveal), and this pane applies it.
///
/// Inputs:   Initialize(services) and SetDocument(doc) once, document reveal requests, and messages
///           (editor actions, diagnostics changed).
/// Outputs:  messages only (StatusMessage, EditorStateChangedMessage).
/// It holds no reference to MainWindow or any view model.
/// </summary>
public partial class EditorPaneView : UserControl,
    IRecipient<DiagnosticsChangedMessage>, IRecipient<EditorActionMessage>
{
    private EditorContext _ctx = null!;
    private IEditorSettings _settings = null!;
    private EditorServices _services = null!;
    private EditorGrammars _grammars = null!;
    private readonly DiagnosticRenderer _diagnosticRenderer = new();
    private BreakpointMargin? _breakpointMargin;
    private TextMateController _textMateController = null!;
    private FoldingController _folding = null!;
    private WhitespaceSelectionController _whitespaceSelection = null!;
    private OccurrenceController _occurrences = null!;
    private TypingController _typing = null!;
    private HoverController _hover = null!;
    private CodeLensController _codeLens = null!;

    public EditorPaneView()
    {
        InitializeComponent();

        // Dock takes a background tab's view off screen and puts it back when shown. Keep its scroll
        // position across that, and apply any reveal asked for while it was off screen.
        DetachedFromVisualTree += (_, _) => SaveViewState();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(RestoreViewState, DispatcherPriority.Loaded);
    }

    // Feature controllers. Private: the outside world reaches the editor through
    // Initialize / SetDocument / Shutdown and messages (EditorActionMessage, navigation...).
    private TextMate.Installation _textMate = null!;
    private DebugValuesController _debugValues = null!;
    private NavigationCommands _navigation = null!;
    private CodeActionController _codeActions = null!;
    private SignatureHelpController _signatureHelp = null!;
    private CompletionController _completion = null!;

    // ------------------------------------------------------------- initialise

    /// <summary>
    /// Call once, from whoever creates the pane (DocumentView).
    /// </summary>
    public void Initialize(EditorServices services)
    {
        _settings = services.Settings;
        _services = services;
        _grammars = services.Grammars;
        _ctx = new EditorContext(Editor, services.Settings, services.Xaml, services.Documents, services.Languages, services.Messenger);

        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        _occurrences = new OccurrenceController(_ctx);
        OverviewRuler.Attach(_ctx, _occurrences);

        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_ctx.Active is { } active)
                active.CaretOffset = Editor.CaretOffset;
            _ctx.Messenger.Send(new EditorStateChangedMessage());
        };

        // Typing or clicking in an editor makes its tab the selected one, in whichever window it is.
        Editor.TextArea.GotFocus += (_, _) =>
        {
            if (_ctx.Active is { } focused)
                _services.Documents.Activate(focused);
        };

        // Keep the document's scroll position current (it is saved with the workspace session).
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            if (_ctx.Active is { } scrolled && TopLevel.GetTopLevel(this) is not null)
            {
                scrolled.VerticalOffset = Editor.VerticalOffset;
                scrolled.HorizontalOffset = Editor.HorizontalOffset;
            }
        };

        Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;

        Editor.Options.EnableTextDragDrop = true;
        Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

        // Ctrl+wheel over the editor changes the font size. (The preview handles its own wheel.)
        Editor.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

        _breakpointMargin = new BreakpointMargin();
        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);

        _codeActions = new CodeActionController(_ctx, services.WorkspaceEdits); // inserts its margin at index 1
        _hover = new HoverController(_ctx, _codeActions, HoverPopup);
        _codeLens = new CodeLensController(_ctx, services.Navigation, ReferencesPopup, ReferencesList, ReferencesTitle);
        _signatureHelp = new SignatureHelpController(_ctx);
        _completion = new CompletionController(_ctx, _hover);
        _typing = new TypingController(_ctx, _hover, _completion, _signatureHelp);
        _debugValues = new DebugValuesController(_ctx, services.DebugSnapshots);
        _navigation = new NavigationCommands(_ctx, services.Dialogs, services.WorkspaceEdits, services.Navigation);
        _folding = new FoldingController(_ctx);
        _whitespaceSelection = new WhitespaceSelectionController(_ctx);

        _textMate = Editor.InstallTextMate(_grammars);
        _textMateController = new TextMateController(_ctx, _textMate, _grammars, _breakpointMargin);

        // Settings are observed here, so nobody has to call ApplyEditorSettings from outside.
        _settings.PropertyChanged += OnSettingsChanged;
        _ctx.Messenger.RegisterAll(this);
        ApplyEditorSettings();
        ApplyTheme();
    }

    public void Shutdown()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _ctx.Messenger.UnregisterAll(this);
        if (_ctx.Active is { } document)
            document.RevealRequested -= OnRevealRequested;
        _debugValues.Dispose();
        _breakpointMargin?.SetDocument(null);
        _folding.Dispose();
        _textMate.Dispose();
    }

    // ------------------------------------------------------------ the one direct input

    /// <summary>
    /// The document this pane shows (null = none). Each pane is made for one document, but swapping
    /// is supported: the view state of the document being left is saved and restored on return.
    /// </summary>
    public void SetDocument(OpenDocument? doc)
    {
        _hover.Hide(resetOffset: true);

        if (_ctx.Active is { } prev) // save view state of the tab we're leaving
        {
            prev.CaretOffset = Editor.CaretOffset;
            prev.VerticalOffset = Editor.VerticalOffset;
            prev.HorizontalOffset = Editor.HorizontalOffset;
            prev.RevealRequested -= OnRevealRequested;
        }

        // FoldingManager installs document-bound generators; detach them while
        // the old document is still attached to the editor.
        _ctx.Detach();

        _breakpointMargin?.SetDocument(doc);

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
        doc.RevealRequested += OnRevealRequested;

        // Scrolling needs a layout pass first. A pending reveal wins over the saved scroll position.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_ctx.Active, doc))
                return;
            if (doc.PendingReveal is not null)
            {
                ApplyPendingReveal();
                return;
            }

            Editor.ScrollToOffset(doc.HorizontalOffset, doc.VerticalOffset);
        }, DispatcherPriority.Loaded);

        if (ReferenceEquals(doc, _services.Documents.Active))
            Editor.Focus();
    }

    // ------------------------------------------------------ view state

    private void SaveViewState()
    {
        if (_ctx?.Active is not { } document)
            return;
        document.CaretOffset = Editor.CaretOffset;
        document.VerticalOffset = Editor.VerticalOffset;
        document.HorizontalOffset = Editor.HorizontalOffset;
    }

    private void RestoreViewState()
    {
        if (_ctx?.Active is not { } document || TopLevel.GetTopLevel(this) is null)
            return;
        if (document.PendingReveal is not null)
        {
            ApplyPendingReveal();
            return;
        }

        Editor.ScrollToOffset(document.HorizontalOffset, document.VerticalOffset);
    }

    // ------------------------------------------------------ reveal (navigation)

    private void OnRevealRequested()
        => Dispatcher.UIThread.Post(ApplyPendingReveal, DispatcherPriority.Loaded);

    /// <summary>Select, scroll to and focus the range the document asked to reveal (once on screen).</summary>
    private void ApplyPendingReveal()
    {
        if (_ctx.Active is not { } document || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;   // not on screen: stays pending until this pane is shown
        if (document.TakePendingReveal() is not { } reveal)
            return;

        Editor.Select(reveal.Offset, reveal.Length);
        var caret = Editor.TextArea.Caret;
        Editor.ScrollTo(caret.Line, caret.Column);

        if (topLevel is Window { IsActive: false } window)
            window.Activate();   // the tab may be in another (floating) window
        Editor.Focus();
    }

    // ------------------------------------------------------ reacting to the outside

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSettings.SelectedTheme))
            ApplyTheme();
        else
            ApplyEditorSettings();
    }

    private void ApplyTheme()
        => _textMate.SetTheme(_grammars.LoadTheme(_settings.SelectedTheme));

    private void ApplyEditorSettings()
    {
        Editor.FontFamily = new FontFamily(_settings.EditorFontFamily);
        Editor.FontSize = _settings.EditorFontSize;
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        Editor.Options.HighlightCurrentLine = _settings.HighlightCurrentLine;
    }

    /// <summary>The active document's diagnostics changed: redraw squiggles, lightbulbs and the overview ruler.</summary>
    public void Receive(DiagnosticsChangedMessage message)
    {
        var document = message.Document;
        if (!ReferenceEquals(document, _ctx.Active)) return;
        _codeActions.RefreshMargin(document);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        OverviewRuler.Refresh();
        _ctx.Messenger.Send(new EditorStateChangedMessage());
    }

    /// <summary>An editor action from a menu or key binding (see EditorCommands).</summary>
    public void Receive(EditorActionMessage message)
    {
        if (!ReferenceEquals(_ctx.Active, _services.Documents.Active))
            return;   // another pane shows the selected tab

        switch (message.Action)
        {
            case EditorAction.GoToDefinition: _ = _navigation.GoToDefinitionAsync(); break;
            case EditorAction.FindReferences: _ = _navigation.FindReferencesAsync(); break;
            case EditorAction.RenameSymbol: _ = _navigation.RenameSymbolAsync(); break;
            case EditorAction.QuickOpen: _ = _navigation.QuickOpenAsync(); break;
            case EditorAction.WorkspaceSymbols: _ = _navigation.SearchWorkspaceSymbolsAsync(); break;
            case EditorAction.QuickActions: _ = _codeActions.ShowCodeActionsAsync(); break;
            case EditorAction.SignatureHelp:
                if (_ctx.Active is { } document)
                    _ = _signatureHelp.RequestAsync(document, triggerCharacter: null);
                break;
            case EditorAction.Completion:
                if (_ctx.Active is { } completionDocument && _ctx.Languages.ConnectionFor(completionDocument) is not null)
                    _ = _completion.ShowCompletionAsync(completionDocument, triggerChar: null);
                break;
            case EditorAction.Undo: Editor.Undo(); break;
            case EditorAction.Redo: Editor.Redo(); break;
            case EditorAction.Cut: Editor.Cut(); break;
            case EditorAction.Copy: Editor.Copy(); break;
            case EditorAction.Paste: Editor.Paste(); break;
            case EditorAction.SelectAll: Editor.SelectAll(); break;
        }
    }

    // -------------------------------------------------------------- handlers

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        var nextSize = _settings.EditorFontSize + (e.Delta.Y > 0 ? 1 : -1);
        _settings.EditorFontSize = Math.Clamp(nextSize, 6, 72);
        e.Handled = true;
    }

    // Context-menu "Quick Actions..." (the View menu uses EditorCommands.QuickActions).
    private void OnContextMenuOpening(object? sender, EventArgs e)
        => RenameMenuItem.IsVisible = _ctx?.Active?.LanguageId == LanguageIds.CSharp;

    private void OnRenameClick(object? sender, RoutedEventArgs e) => _ = _navigation.RenameSymbolAsync();

    private async void OnCodeActionsClick(object? sender, RoutedEventArgs e)
        => await _codeActions.ShowCodeActionsAsync(anchor: Editor);

    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx.Active is { } document)
            _ = _services.Languages.FormatDocumentAsync(document, reportUnchanged: true);
    }

    // Pure view concerns, fine to stay in code-behind.
    private void OnCopyClick(object? sender, RoutedEventArgs e)
        => ApplicationCommands.Copy.Execute(null, Editor.TextArea);

    private void OnCutClick(object? sender, RoutedEventArgs e)
        => ApplicationCommands.Cut.Execute(null, Editor.TextArea);

    private void OnPasteClick(object? sender, RoutedEventArgs e)
        => ApplicationCommands.Paste.Execute(null, Editor.TextArea);

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
        => ApplicationCommands.SelectAll.Execute(null, Editor.TextArea);
}
