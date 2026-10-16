using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using AXamlIDE.Models;

namespace AXamlIDE.Services;

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
    public event Action<OpenDocument>? Saved;     // -> didSave

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
        doc.Dispose();
        return true;
    }

    public void Save(OpenDocument doc)
    {
        if (doc.IsReadOnly)
            throw new InvalidOperationException("This document is read-only.");

        doc.Sync?.Flush();    // make sure the server has the latest text first
        doc.Save();
        Saved?.Invoke(doc);
    }

    public OpenDocument? ByUri(string uri) => Documents.FirstOrDefault(d => d.Uri == uri);

    private static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".axaml" or ".xaml" => "axaml",
        ".cs" => "csharp",
        ".csproj" or ".props" or ".targets" => "xml",
        ".json" => "json",
        _ => "plaintext"
    };
}
