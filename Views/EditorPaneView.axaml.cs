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
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.Messaging;
using TextMateSharp.Grammars;

namespace AXamlIDE.Views;

/// <summary>
/// The editor, as a pane. Owns the TextEditor, its margins and renderers, TextMate, and every
/// editor feature controller (completion, hover, folding, navigation...).
///
/// Inputs:   Initialize(services) once, SetDocument() on every tab change, and messages
///           (navigation, debug snapshot, diagnostics changed).
/// Outputs:  messages only (StatusMessage, EditorStateChangedMessage, EditorThemeAppliedMessage).
/// It holds no reference to MainWindow or any view model.
/// </summary>
public partial class EditorPaneView : UserControl,
    IRecipient<DiagnosticsChangedMessage>, IRecipient<EditorActionMessage>
{
    private EditorContext _ctx = null!;
    private IEditorSettings _settings = null!;
    private EditorServices _services = null!;
    private RegistryOptions _registryOptions = null!;
    private readonly DiagnosticRenderer _diagnosticRenderer = new();
    private BreakpointMargin? _breakpointMargin;
    private TextMateController _textMateController = null!;
    private FoldingController _folding = null!;
    private OccurrenceController _occurrences = null!;
    private TypingController _typing = null!;
    private HoverController _hover = null!;

    public EditorPaneView()
    {
        InitializeComponent();
    }

    // Feature controllers. Private: the outside world reaches the editor through
    // Initialize / SetDocument / Shutdown and messages (EditorActionMessage, navigation...).
    private TextMate.Installation _textMate = null!;
    private EditorNavigator _navigator = null!;
    private NavigationCommands _navigation = null!;
    private WorkspaceEditService _workspaceEdits = null!;
    private CodeActionController _codeActions = null!;
    private SignatureHelpController _signatureHelp = null!;
    private CompletionController _completion = null!;

    // ------------------------------------------------------------- initialise

    /// <summary>
    /// Call once, from the hosting DocumentWellView. It applies the current theme, so register for
    /// EditorThemeAppliedMessage (and EditorStateChangedMessage) BEFORE calling this.
    /// </summary>
    public void Initialize(RegistryOptions registryOptions, EditorServices services)
    {
        _settings = services.Settings;
        _services = services;
        _registryOptions = registryOptions;
        _ctx = new EditorContext(Editor, services.Settings, services.Xaml, services.Documents, services.Languages, services.Messenger);

        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        _occurrences = new OccurrenceController(_ctx);

        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_ctx.Active is { } active)
                active.CaretOffset = Editor.CaretOffset;
            _ctx.Messenger.Send(new EditorStateChangedMessage());
        };

        Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;

        Editor.Options.EnableTextDragDrop = true;
        Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

        // Ctrl+wheel over the editor changes the font size. (The preview handles its own wheel.)
        Editor.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

        _breakpointMargin = new BreakpointMargin();
        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);

        _workspaceEdits = new WorkspaceEditService(_ctx);
        services.Languages.WorkspaceEditRequested = _workspaceEdits.ApplyServerEditAsync;   // server-pushed edits
        _codeActions = new CodeActionController(_ctx, _workspaceEdits); // inserts its margin at index 1
        _hover = new HoverController(_ctx, _codeActions);
        _signatureHelp = new SignatureHelpController(_ctx);
        _completion = new CompletionController(_ctx, _hover);
        _typing = new TypingController(_ctx, _hover, _completion, _signatureHelp);
        _navigator = new EditorNavigator(_ctx);                 // registers for navigation messages
        _navigation = new NavigationCommands(_ctx, services.Dialogs, _workspaceEdits, _navigator);
        _folding = new FoldingController(_ctx);

        _textMate = Editor.InstallTextMate(registryOptions);
        _textMateController = new TextMateController(_ctx, _textMate, registryOptions, _breakpointMargin);
        _textMateController.ThemeApplied += e => _ctx.Messenger.Send(new EditorThemeAppliedMessage(e));

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
        _navigator.Dispose();
        _breakpointMargin?.SetDocument(null);
        _folding.Dispose();
        _textMate.Dispose();
    }

    // ------------------------------------------------------------ the one direct input

    /// <summary>
    /// Call on every tab change (null = no tab open). Stays a direct call, not a message,
    /// because it has to run in a fixed order relative to the rest of the tab-change handling.
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

        // Scrolling needs a layout pass first.
        Dispatcher.UIThread.Post(() =>
        {
            Editor.ScrollToVerticalOffset(doc.VerticalOffset);
            Editor.ScrollToHorizontalOffset(doc.HorizontalOffset);
        }, DispatcherPriority.Loaded);

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
        => _textMate.SetTheme(_settings.SelectedTheme.Load(_registryOptions));

    private void ApplyEditorSettings()
    {
        Editor.FontFamily = new FontFamily(_settings.EditorFontFamily);
        Editor.FontSize = _settings.EditorFontSize;
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        Editor.Options.HighlightCurrentLine = _settings.HighlightCurrentLine;
    }

    /// <summary>The active document's diagnostics changed: redraw squiggles and lightbulbs.</summary>
    public void Receive(DiagnosticsChangedMessage message)
    {
        var document = message.Document;
        if (!ReferenceEquals(document, _ctx.Active)) return;
        _codeActions.RefreshMargin(document);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _ctx.Messenger.Send(new EditorStateChangedMessage());
    }

    /// <summary>An editor action from a menu or key binding (see EditorCommands).</summary>
    public void Receive(EditorActionMessage message)
    {
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
