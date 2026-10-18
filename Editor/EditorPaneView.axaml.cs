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
using AXamlIDE.Documents;
using AXamlIDE.Editor.CodeActions;
using AXamlIDE.Editor.CodeCompletion;
using AXamlIDE.Editor.Debugging;
using AXamlIDE.Editor.Folding;
using AXamlIDE.Editor.Highlighting;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Editor.Navigation;
using AXamlIDE.Editor.SignatureHelp;
using AXamlIDE.Settings;
using CommunityToolkit.Mvvm.Messaging;
using TextMateSharp.Grammars;

namespace AXamlIDE.Editor;

/// <summary>
/// The editor, as a pane. Owns the TextEditor, its margins and renderers, TextMate, and every
/// editor feature controller (completion, hover, folding, navigation...).
///
/// Inputs:   Initialize(services) once, SetDocument() on every tab change, and messages
///           (navigation, debug snapshot, diagnostics changed).
/// Outputs:  messages only (StatusMessage, EditorStateChangedMessage, EditorThemeAppliedMessage).
/// It holds no reference to MainWindow or any view model.
/// </summary>
public partial class EditorPaneView : UserControl, IRecipient<DiagnosticsChangedMessage>
{
    private EditorContext _ctx = null!;
    private IEditorSettings _settings = null!;
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
    /// Call once, from the MainWindow constructor. It applies the current theme, so register for
    /// EditorThemeAppliedMessage (and EditorStateChangedMessage) BEFORE calling this.
    /// </summary>
    public void Initialize(RegistryOptions registryOptions, EditorServices services)
    {
        _settings = services.Settings;
        _registryOptions = registryOptions;
        _ctx = new EditorContext(Editor, services.Settings, services.Xaml, services.Documents, services.Messenger);

        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        _occurrences = new OccurrenceController(_ctx);

        Editor.TextArea.Caret.PositionChanged += (_, _) => _ctx.Messenger.Send(new EditorStateChangedMessage());

        Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;

        Editor.Options.EnableTextDragDrop = true;
        Editor.Options.CompletionAcceptAction = CompletionAcceptAction.DoubleTapped;

        // Ctrl+wheel over the editor changes the font size. (The preview handles its own wheel.)
        Editor.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, true);

        _breakpointMargin = new BreakpointMargin();
        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);

        WorkspaceEdits = new WorkspaceEditService(_ctx);
        CodeActions = new CodeActionController(_ctx, WorkspaceEdits); // inserts its margin at index 1
        _hover = new HoverController(_ctx, CodeActions);
        SignatureHelp = new SignatureHelpController(_ctx);
        Completion = new CompletionController(_ctx, _hover);
        _typing = new TypingController(_ctx, _hover, Completion, SignatureHelp);
        Navigator = new EditorNavigator(_ctx);                 // registers for navigation messages
        Navigation = new NavigationCommands(_ctx, services.Dialogs, WorkspaceEdits, Navigator);
        _folding = new FoldingController(_ctx);

        TextMateInstallation = Editor.InstallTextMate(registryOptions);
        _textMateController = new TextMateController(_ctx, TextMateInstallation, registryOptions, _breakpointMargin);
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
        Navigator.Dispose();
        _breakpointMargin?.SetDocument(null);
        _folding.Dispose();
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
        => TextMateInstallation.SetTheme(_settings.SelectedTheme.Load(_registryOptions));

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
        CodeActions.RefreshMargin(document);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _ctx.Messenger.Send(new EditorStateChangedMessage());
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

    // Context-menu "Quick Actions..." (the View menu has its own handler in MainWindow).
    private async void OnCodeActionsClick(object? sender, RoutedEventArgs e)
        => await CodeActions.ShowCodeActionsAsync(anchor: Editor);

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
