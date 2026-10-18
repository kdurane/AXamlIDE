using System;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Views.Editor;

/// <summary>
/// The hub every editor controller shares: the TextEditor, the active document, and the
/// document lifecycle events. It knows nothing about MainWindow or any view model.
/// Everything outside the editor is reached through a narrow interface or a message.
///
/// Call order (done by EditorPaneView.SetDocument):
///   Detach();                        // BEFORE Editor.Document is swapped (old doc still attached)
///   Editor.Document = doc.Document;
///   Attach(doc);                     // AFTER the swap (Attach(null) when no tab is open)
/// </summary>
public sealed class EditorContext(
    TextEditor editor,
    IEditorSettings settings,
    XamlLanguageService xaml,
    IDocumentHost documents,
    IMessenger messenger)
{
    private TextDocument? _hooked;

    public TextEditor Editor { get; } = editor;
    public IEditorSettings Settings { get; } = settings;
    public XamlLanguageService Xaml { get; } = xaml;

    /// <summary>Opens and activates documents (implemented by the main window).</summary>
    public IDocumentHost Documents { get; } = documents;

    public IMessenger Messenger { get; } = messenger;

    /// <summary>Single place controllers report status.</summary>
    public void Status(string text) => Messenger.Send(new StatusMessage(text));

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
