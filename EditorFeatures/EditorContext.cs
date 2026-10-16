using System;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AXaxmlIDE.Models;
using AXaxmlIDE.ViewModels;

namespace AXaxmlIDE.EditorFeatures;

/// <summary>
/// The small hub every editor controller shares: the TextEditor, the active document,
/// and three events. MainWindow tells it when the tab changes; controllers listen.
///
/// Call order inside MainWindow.OnTabSelectionChanged:
///   _ctx.Detach();                   // BEFORE Editor.Document is swapped (old doc still attached)
///   Editor.Document = doc.Document;  // existing line
///   _ctx.Attach(doc);                // AFTER the swap (Attach(null) when no tab is selected)
/// </summary>
public sealed class EditorContext
{
    private TextDocument? _hooked;

    private readonly Func<MainWindowViewModel> _viewModel;

    public EditorContext(TextEditor editor, Func<MainWindowViewModel> viewModel)
    {
        Editor = editor;
        _viewModel = viewModel;
    }

    public TextEditor Editor { get; }

    /// <summary>
    /// TEMPORARY coupling: controllers reach the view model through here so their bodies stay
    /// copy-paste from MainWindow. Later this becomes narrow interfaces / messages.
    /// </summary>
    public MainWindowViewModel ViewModel => _viewModel();

    /// <summary>Single place controllers report status. Swap for a messenger send later.</summary>
    public void Status(string text) => ViewModel.ShowStatusMessage(text);

    /// <summary>
    /// How to make a document the active tab. MainWindow sets this to
    /// <c>doc => DocTabs.SelectedItem = doc</c>. Selecting the tab swaps the editor
    /// synchronously, exactly as the old inline code relied on.
    /// </summary>
    public Action<OpenDocument> ActivateDocument { get; set; } = _ => { };

    /// <summary>Was <c>DocTabs.SelectedItem = ViewModel.OpenDocument(path)</c> (about eight copies).</summary>
    public OpenDocument OpenAndActivate(string path)
    {
        var document = ViewModel.OpenDocument(path);
        ActivateDocument(document);
        return document;
    }

    /// <summary>The document currently shown in the editor (null between Detach and Attach).</summary>
    public OpenDocument? Active { get; private set; }

    /// <summary>Raised before the editor swaps documents. Argument is the document being left.</summary>
    public event Action<OpenDocument?>? Detaching;

    /// <summary>Raised after the editor has the new document. Argument is null when no tab is open.</summary>
    public event Action<OpenDocument?>? Attached;

    /// <summary>Raised on every text change of the active document only.</summary>
    public event Action<OpenDocument>? TextChanged;

    public void Detach()
    {
        Detaching?.Invoke(Active);

        if (_hooked is not null)
        {
            _hooked.Changed -= OnDocumentChanged;
            _hooked = null;
        }

        Active = null;
    }

    public void Attach(OpenDocument? doc)
    {
        Active = doc;
        _hooked = doc?.Document;
        if (_hooked is not null)
            _hooked.Changed += OnDocumentChanged;

        Attached?.Invoke(doc);
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (Active is { } doc)
            TextChanged?.Invoke(doc);
    }
}
