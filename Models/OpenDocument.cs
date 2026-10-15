using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using AvaloniaEdit.Document;
using AXaxmlIDE.Lsp;

namespace AXaxmlIDE.Models;

/// <summary>Everything the IDE knows about one open file.</summary>
public sealed class OpenDocument : INotifyPropertyChanged, IDisposable
{
    private readonly List<ITextAnchor> _breakpoints = [];

    public string Path { get; }
    public string Uri => new Uri(Path).AbsoluteUri;                 // file:///home/...
    public string Title => System.IO.Path.GetFileName(Path);
    public string DisplayTitle => IsDirty ? Title + " \u2022" : Title;
    public string LanguageId { get; }                                // "axaml", "csharp", ...
    public Encoding Encoding { get; }                                // preserved on save
    public bool IsReadOnly { get; }

    public TextDocument Document { get; }
    public IReadOnlyList<int> BreakpointLines
        =>
        [
            .. _breakpoints.Where(anchor => !anchor.IsDeleted)
                .Select(anchor => Document.GetLineByOffset(anchor.Offset).LineNumber)
                .Distinct()
                .OrderBy(line => line)
        ];

    // Per-document: a TextSegmentCollection is bound to its TextDocument.
    public TextSegmentCollection<DiagnosticSegment> Diagnostics { get; }
    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }

    /// <summary>The server this document was opened on; null if its language has no server.</summary>
    public LspClient? Client { get; set; }

    /// <summary>Non-null once that server has been told about this document (didOpen sent).</summary>
    public DocumentSync? Sync { get; set; }

    /// <summary>The in-flight pull-diagnostics request, if any (cancelled when a newer one starts).</summary>
    public CancellationTokenSource? PullCts { get; set; }

    // View state, saved on tab switch and restored on return.
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }

    public bool IsDirty => !Document.UndoStack.IsOriginalFile;

    public OpenDocument(string path, string text, Encoding encoding, string languageId, bool isReadOnly = false)
    {
        Path = path;
        Encoding = encoding;
        LanguageId = languageId;
        IsReadOnly = isReadOnly;

        Document = new TextDocument(text);
        Document.UndoStack.MarkAsOriginalFile();
        Diagnostics = new TextSegmentCollection<DiagnosticSegment>(Document);

        // Undoing back to the saved state clears the dirty marker too.
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Document.UndoStack.IsOriginalFile)) return;
            Raise(nameof(IsDirty));
            Raise(nameof(DisplayTitle));
        };
    }

    public void Save()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("This document is read-only.");

        File.WriteAllText(Path, Document.Text, Encoding);
        Document.UndoStack.MarkAsOriginalFile();
    }

    public void ToggleBreakpoint(int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > Document.LineCount)
            return;

        var lineOffset = Document.GetLineByNumber(lineNumber).Offset;
        var existing = _breakpoints.FindIndex(anchor =>
            !anchor.IsDeleted && Document.GetLineByOffset(anchor.Offset).LineNumber == lineNumber);

        if (existing >= 0)
            _breakpoints.RemoveAt(existing);
        else
        {
            var anchor = Document.CreateAnchor(lineOffset);
            anchor.SurviveDeletion = true;
            _breakpoints.Add(anchor);
        }

        BreakpointsChanged?.Invoke();
    }

    /// <summary>
    /// Returns false (and changes nothing) if the result is stale: the user has typed
    /// since the server analysed, so its offsets would be wrong. The server will
    /// publish again after it has seen the newer text.
    /// </summary>
    public bool ApplyDiagnostics(IReadOnlyList<LspDiagnostic> list, int? serverVersion)
    {
        if (Sync is { } sync)
        {
            if (sync.HasPendingChanges) return false;
            if (serverVersion is { } v && v != sync.Version) return false;
        }

        Diagnostics.Clear();
        int errors = 0, warnings = 0;

        foreach (var d in list)
        {
            if (d.Severity >= 4) continue;   // hints (unused usings, style suggestions) aren't drawn

            var start = ToOffset(d.StartLine, d.StartChar);
            var end = ToOffset(d.EndLine, d.EndChar);

            // Zero-length ranges don't render; widen to one character.
            if (end <= start) end = Math.Min(start + 1, Document.TextLength);
            if (end <= start) continue;

            Diagnostics.Add(new DiagnosticSegment
            {
                StartOffset = start,
                EndOffset = end,
                Message = d.Message,
                Severity = d.Severity,
                RawDiagnostic = d.Raw
            });

            if (d.Severity == 1) errors++;
            else if (d.Severity == 2) warnings++;
        }

        ErrorCount = errors;
        WarningCount = warnings;
        return true;
    }

    public void ClearDiagnostics()
    {
        Diagnostics.Clear();
        ErrorCount = WarningCount = 0;
    }

    /// <summary>LSP (0-based line, UTF-16 char) to a document offset, clamped to the line.</summary>
    public int OffsetOf(int line0, int char0) => ToOffset(line0, char0);

    private int ToOffset(int line0, int char0)
    {
        if (line0 >= Document.LineCount) return Document.TextLength;
        var line = Document.GetLineByNumber(line0 + 1);
        return line.Offset + Math.Min(char0, line.Length);
    }

    public void Dispose()
    {
        PullCts?.Cancel();
        Sync?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? BreakpointsChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}