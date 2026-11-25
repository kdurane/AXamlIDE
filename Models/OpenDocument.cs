using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AvaloniaEdit.Document;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Search;

namespace AXamlIDE.Models;

/// <summary>Everything the IDE knows about one open file.</summary>
public sealed class OpenDocument : INotifyPropertyChanged, ITextBuffer
{
    /// <summary>A breakpoint: anchored to its line's start so it moves with edits, plus VS's Conditions and enabled state.</summary>
    private sealed class BreakpointEntry(ITextAnchor anchor)
    {
        public ITextAnchor Anchor { get; } = anchor;
        public string? Condition { get; set; }
        public string? HitCondition { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    private readonly List<BreakpointEntry> _breakpoints = [];

    public string Path { get; }
    public string Uri => new Uri(Path).AbsoluteUri;                 // file:///home/...
    public string Title => System.IO.Path.GetFileName(Path);
    public string DisplayTitle => IsDirty ? Title + " \u2022" : Title;
    public string LanguageId { get; }                                // a LanguageIds value
    public Encoding Encoding { get; }                                // preserved on save
    public bool IsReadOnly { get; }

    public TextDocument Document { get; }
    /// <summary>The lines with an enabled breakpoint (what the debugger stops at).</summary>
    public IReadOnlyList<int> BreakpointLines => [.. Breakpoints.Where(b => b.IsEnabled).Select(b => b.Line)];

    /// <summary>Every breakpoint, one per line, in line order (the margin draws these; disabled ones hollow).</summary>
    public IReadOnlyList<BreakpointInfo> Breakpoints
        =>
        [
            .. _breakpoints.Where(entry => !entry.Anchor.IsDeleted)
                .GroupBy(entry => LineOf(entry))
                .Select(group =>
                {
                    var entry = group.First();
                    return new BreakpointInfo(group.Key, entry.IsEnabled, entry.Condition, entry.HitCondition);
                })
                .OrderBy(info => info.Line)
        ];

    /// <summary>The enabled breakpoints with their conditions, as the debugger is told them.</summary>
    public IReadOnlyList<Services.Debugging.SourceBreakpoint> DebuggerBreakpoints
        => [.. Breakpoints.Where(b => b.IsEnabled).Select(b => new Services.Debugging.SourceBreakpoint(b.Line, b.Condition, b.HitCondition))];

    /// <summary>The breakpoint on a line, or null.</summary>
    public BreakpointInfo? BreakpointAt(int lineNumber) => Breakpoints.FirstOrDefault(b => b.Line == lineNumber);

    private int LineOf(BreakpointEntry entry) => Document.GetLineByOffset(entry.Anchor.Offset).LineNumber;

    private BreakpointEntry? EntryAt(int lineNumber)
        => _breakpoints.FirstOrDefault(entry => !entry.Anchor.IsDeleted && LineOf(entry) == lineNumber);

    /// <summary>The breakpoint's Conditions (empty or null clears them).</summary>
    public void SetBreakpointConditions(int lineNumber, string? condition, string? hitCondition)
    {
        if (EntryAt(lineNumber) is not { } entry)
            return;
        entry.Condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        entry.HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition.Trim();
        BreakpointsChanged?.Invoke();
    }

    /// <summary>VS's Disable / Enable Breakpoint: a disabled one stays in the margin but doesn't stop.</summary>
    public void SetBreakpointEnabled(int lineNumber, bool enabled)
    {
        if (EntryAt(lineNumber) is not { } entry || entry.IsEnabled == enabled)
            return;
        entry.IsEnabled = enabled;
        BreakpointsChanged?.Invoke();
    }

    /// <summary>
    /// Put back breakpoints the file had when it was last closed (or in the last session), with their state and
    /// Conditions. Lines past the end (the file got shorter outside the IDE) and lines that already have one are skipped.
    /// </summary>
    public void RestoreBreakpoints(IEnumerable<BreakpointInfo> breakpoints)
    {
        var added = false;
        foreach (var breakpoint in breakpoints)
        {
            if (breakpoint.Line < 1 || breakpoint.Line > Document.LineCount || EntryAt(breakpoint.Line) is not null)
                continue;
            var anchor = Document.CreateAnchor(Document.GetLineByNumber(breakpoint.Line).Offset);
            anchor.SurviveDeletion = true;
            _breakpoints.Add(new BreakpointEntry(anchor)
            {
                IsEnabled = breakpoint.IsEnabled,
                Condition = breakpoint.Condition,
                HitCondition = breakpoint.HitCondition
            });
            added = true;
        }
        if (added)
            BreakpointsChanged?.Invoke();
    }

    /// <summary>Debug > Delete All Breakpoints (for this document).</summary>
    public void ClearBreakpoints()
    {
        if (_breakpoints.Count == 0)
            return;
        _breakpoints.Clear();
        BreakpointsChanged?.Invoke();
    }

    // Per-document: a TextSegmentCollection is bound to its TextDocument.
    public TextSegmentCollection<DiagnosticSegment> Diagnostics { get; }
    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }

    // Its language-server connection (if any) is kept by LanguageSessionService, not here.

    // View state. CaretOffset is kept current by the editor while the document is active;
    // the scroll offsets are saved on tab switch. All are restored when the tab returns.
    public int CaretOffset { get; set; }

    /// <summary>The selected text's range (Length 0 = nothing selected), kept current like <see cref="CaretOffset"/>.</summary>
    public (int Offset, int Length) Selection { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }

    /// <summary>
    /// The lines whose fold is collapsed, kept current by the editor's folding (saved with the workspace session,
    /// as VS and Rider remember outlining). Null until an editor has shown the file: its default folds apply.
    /// </summary>
    public IReadOnlyList<int>? CollapsedFolds { get; set; }

    /// <summary>Folds restored by the session that the editor hasn't applied yet (see <see cref="RestoreFolds"/>).</summary>
    public IReadOnlyList<int>? PendingFolds { get; private set; }

    /// <summary>Raised by <see cref="RestoreFolds"/>; the editor showing this document applies them.</summary>
    public event Action? FoldsRestoreRequested;

    /// <summary>Collapse the folds starting on these lines (and expand the rest) once an editor has computed them.</summary>
    public void RestoreFolds(IReadOnlyList<int> lines)
    {
        CollapsedFolds = PendingFolds = lines;
        FoldsRestoreRequested?.Invoke();
    }

    /// <summary>The editor has applied <see cref="PendingFolds"/>.</summary>
    public void ClearPendingFolds() => PendingFolds = null;

    public bool IsDirty => !Document.UndoStack.IsOriginalFile;

    /// <summary>
    /// True while a code action's edits (a quick fix adding a using, say) are applied: the editor showing this
    /// document keeps its view still for edits above it. Other edits (typing, undo, redo) scroll as usual.
    /// </summary>
    public bool KeepViewStill { get; private set; }

    /// <summary>Runs <paramref name="edit"/> with <see cref="KeepViewStill"/> set.</summary>
    public void EditKeepingView(Action edit)
    {
        KeepViewStill = true;
        try
        {
            edit();
        }
        finally
        {
            KeepViewStill = false;
        }
    }

    /// <summary>
    /// A "show the user this range" request (from navigation, search, debugging...) that the editor
    /// showing this document hasn't applied yet. Navigation never needs to know which editor that is,
    /// or whether one exists yet: a newly created editor applies the pending request when it opens.
    /// </summary>
    public (int Offset, int Length)? PendingReveal { get; private set; }

    /// <summary>Raised by <see cref="Reveal"/>; the editor showing this document applies it.</summary>
    public event Action? RevealRequested;

    /// <summary>Ask the editor showing this document to select the range, scroll to it and take focus.</summary>
    public void Reveal(int offset, int length = 0)
    {
        offset = Math.Clamp(offset, 0, Document.TextLength);
        PendingReveal = (offset, Math.Clamp(length, 0, Document.TextLength - offset));
        CaretOffset = offset;
        RevealRequested?.Invoke();
    }

    /// <summary>Returns the pending reveal request (if any) and clears it.</summary>
    public (int Offset, int Length)? TakePendingReveal()
    {
        var pending = PendingReveal;
        PendingReveal = null;
        return pending;
    }

    /// <summary>The whole text (<see cref="ITextBuffer"/>, used by replace-in-files).</summary>
    string ITextBuffer.Text
    {
        get => Document.Text;
        set => Document.Text = value;
    }

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
        ChangedOnDisk = false;   // what's on disk is ours now
        DeletedOnDisk = false;
    }

    public void ToggleBreakpoint(int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > Document.LineCount)
            return;

        var lineOffset = Document.GetLineByNumber(lineNumber).Offset;
        if (_breakpoints.RemoveAll(entry => !entry.Anchor.IsDeleted && LineOf(entry) == lineNumber) == 0)
        {
            var anchor = Document.CreateAnchor(lineOffset);
            anchor.SurviveDeletion = true;
            _breakpoints.Add(new BreakpointEntry(anchor));
        }

        BreakpointsChanged?.Invoke();
    }

    /// <summary>
    /// Replace the diagnostics with a server result. The caller (LanguageSessionService) has already
    /// checked the result isn't stale.
    /// </summary>
    public void ApplyDiagnostics(IReadOnlyList<LspDiagnostic> list)
    {
        Diagnostics.Clear();
        int errors = 0, warnings = 0;

        foreach (var d in list)
        {
            // Hints are left out (Visual Studio's "hidden" severity), except unnecessary code: an unused using or
            // private member is faded in the editor and listed under Messages, as Visual Studio and Rider do.
            var unnecessary = LspHelpers.HasDiagnosticTag(d.Raw, LspDiagnosticTag.Unnecessary);
            if (d.Severity >= LspDiagnosticSeverity.Hint && !unnecessary) continue;

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
                RawDiagnostic = d.Raw,
                IsUnnecessary = unnecessary
            });

            if (d.Severity == LspDiagnosticSeverity.Error) errors++;
            else if (d.Severity == LspDiagnosticSeverity.Warning) warnings++;
        }

        ErrorCount = errors;
        WarningCount = warnings;
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

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? BreakpointsChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>The file changed on disk while this had unsaved edits (the tab offers Reload / Keep mine).</summary>
    public bool ChangedOnDisk { get; private set { field = value; Raise(); } }

    /// <summary>The file was deleted on disk (the tab stays open; saving writes it again).</summary>
    public bool DeletedOnDisk { get; private set { field = value; Raise(); } }

    /// <summary>
    /// Take the text from disk. Only the part that differs is replaced, so the caret, scroll position and
    /// breakpoints stay put; it's one undo step, so Ctrl+Z gets the old text back.
    /// </summary>
    public void ReloadFrom(string text)
    {
        var old = Document.Text;
        var prefix = 0;
        var max = Math.Min(old.Length, text.Length);
        while (prefix < max && old[prefix] == text[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < max - prefix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix])
            suffix++;

        if (old.Length != text.Length || prefix < old.Length)
            Document.Replace(prefix, old.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));

        Document.UndoStack.MarkAsOriginalFile();   // matches the disk again, so not dirty
        ChangedOnDisk = false;
        DeletedOnDisk = false;
    }

    /// <summary>Read the file again (see <see cref="ReloadFrom"/>); false when it can't be read just now.</summary>
    public bool ReloadFromDisk()
    {
        string text;
        try
        {
            text = File.ReadAllText(Path, Encoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // locked mid-write or gone again; the watcher reports the next change
        }

        ReloadFrom(text);
        return true;
    }

    /// <summary>"Keep mine": keep the edits; saving will overwrite the disk.</summary>
    public void KeepOwnChanges() => ChangedOnDisk = false;

    public void MarkChangedOnDisk() => ChangedOnDisk = true;
    public void MarkDeletedOnDisk() => DeletedOnDisk = true;
}

/// <summary>One breakpoint as the margin and menus see it: its line, whether it's on, and its Conditions.</summary>
public sealed record BreakpointInfo(int Line, bool IsEnabled, string? Condition, string? HitCondition)
{
    public bool HasCondition => Condition is not null || HitCondition is not null;
}
