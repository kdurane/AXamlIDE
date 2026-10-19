using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// Single source of truth for open files. The LSP layer subscribes to the events
/// and never touches the tab strip directly.
/// </summary>
public sealed class DocumentManager
{
    private readonly Dictionary<string, OpenDocument> _byPath = [];

    public ObservableCollection<OpenDocument> Documents { get; } = [];

    public event Action<OpenDocument>? Opened;    // -> didOpen + create DocumentSync
    public event Action<OpenDocument>? Closing;   // -> didClose
    public event Action<OpenDocument>? Saving;    // -> flush pending didChange
    public event Action<OpenDocument>? Saved;     // -> didSave

    /// <summary>Raised when the active (front) tab changes; the argument is null when no tab is open.</summary>
    public event Action<OpenDocument?>? ActiveChanged;

    /// <summary>
    /// <see cref="BringToFront"/> on the document that's already <see cref="Active"/>: its tab should come to the
    /// front anyway (a page tab, like a diff or a debugger visualizer, may be in front of it).
    /// </summary>
    public event Action<OpenDocument>? FrontRequested;

    /// <summary>Make the document the active one and show its tab, even if it already was (navigation does this).</summary>
    public void BringToFront(OpenDocument document)
    {
        if (ReferenceEquals(Active, document))
            FrontRequested?.Invoke(document);
        else
            Active = document;
    }

    /// <summary>
    /// The document in the front tab, or null. Set by the shell on tab change. Its CaretOffset is
    /// kept current by the editor, so other areas can ask "where is the user?" without the editor.
    /// </summary>
    public OpenDocument? Active
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            ActiveChanged?.Invoke(value);
        }
    }

    public OpenDocument Open(string path)
    {
        if (_byPath.TryGetValue(path, out var existing))
            return existing;                       // already open: just activate it

        // StreamReader detects BOMs so we can write back in the same encoding.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var doc = new OpenDocument(path, text, reader.CurrentEncoding, LanguageFor(path));

        _byPath[path] = doc;
        Documents.Add(doc);
        Opened?.Invoke(doc);
        return doc;
    }

    public OpenDocument OpenReadOnly(string path, string text, string languageId)
    {
        if (_byPath.TryGetValue(path, out var existing))
            return existing;

        var doc = new OpenDocument(path, text, Encoding.UTF8, languageId, isReadOnly: true);
        _byPath[path] = doc;
        Documents.Add(doc);
        Opened?.Invoke(doc);
        return doc;
    }

    /// <summary>Returns false if the document is dirty and <paramref name="force"/> is not set.</summary>
    public bool Close(OpenDocument doc, bool force = false)
    {
        if (doc.IsDirty && !force) return false;

        Closing?.Invoke(doc);
        _byPath.Remove(doc.Path);
        Documents.Remove(doc);
        if (ReferenceEquals(Active, doc))
            Active = null;
        return true;
    }

    public void Save(OpenDocument doc)
    {
        if (doc.IsReadOnly)
            throw new InvalidOperationException("This document is read-only.");

        Saving?.Invoke(doc);  // e.g. flush pending edits to the language server first
        doc.Save();
        Saved?.Invoke(doc);
    }

    public OpenDocument? ByUri(string uri) => Documents.FirstOrDefault(d => d.Uri == uri);

    private static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".axaml" or ".xaml" => LanguageIds.Axaml,
        ".cs" => LanguageIds.CSharp,
        ".csproj" or ".props" or ".targets" or ".manifest" => LanguageIds.Xml,
        ".json" => LanguageIds.Json,
        _ => LanguageIds.PlainText
    };
}
