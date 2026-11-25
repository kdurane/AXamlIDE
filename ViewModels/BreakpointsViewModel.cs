using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>One row of the Breakpoints list: a breakpoint in a file, open or not. Its check box turns it on and off.</summary>
public sealed partial class BreakpointRow(string path, BreakpointInfo info, Action<BreakpointRow, bool> setEnabled) : ObservableObject
{
    private bool _updating;

    public string Path { get; } = path;

    [ObservableProperty] public partial int Line { get; private set; } = info.Line;
    [ObservableProperty] public partial bool IsEnabled { get; set; } = info.IsEnabled;

    /// <summary>"when count > 3, hit count 2", or null with no Conditions.</summary>
    [ObservableProperty] public partial string? ConditionText { get; private set; } = ConditionsOf(info);

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The full path and line, and the Conditions in full.</summary>
    public string ToolTip => ConditionText is { } conditions ? $"{Path}:{Line}\n{conditions}" : $"{Path}:{Line}";

    /// <summary>The check box: tell the file's document, or the closed files' breakpoints (they tell the debugger).</summary>
    partial void OnIsEnabledChanged(bool value)
    {
        if (!_updating)
            setEnabled(this, value);
    }

    /// <summary>Take on the document's current state for this breakpoint (its line moves with edits).</summary>
    public void Update(BreakpointInfo info)
    {
        _updating = true;
        try
        {
            Line = info.Line;
            IsEnabled = info.IsEnabled;
            ConditionText = ConditionsOf(info);
            OnPropertyChanged(nameof(ToolTip));
        }
        finally
        {
            _updating = false;
        }
    }

    private static string? ConditionsOf(BreakpointInfo info)
    {
        var parts = new List<string>();
        if (info.Condition is { } condition)
            parts.Add($"when {condition}");
        if (info.HitCondition is { } hits)
            parts.Add($"hit count {hits}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}

/// <summary>
/// The Debug tab's Breakpoints section (VS Code's Breakpoints view, Visual Studio's Breakpoints window): every
/// breakpoint, in open documents and in closed files (ClosedFileBreakpoints), by file then line, kept up to date
/// as breakpoints are set, switched and moved by edits. A check box turns one on or off; double-click or Enter goes to it; Delete removes it. It also has
/// the Debug menu's Delete All and Enable / Disable All.
/// </summary>
public sealed partial class BreakpointsViewModel : ObservableObject
{
    private readonly DocumentManager _documents;
    private readonly ClosedFileBreakpoints _closed;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private readonly HashSet<OpenDocument> _watched = [];
    private bool _refreshPending;

    public BreakpointsViewModel(DocumentManager documents, ClosedFileBreakpoints closed, IDialogService dialogs, IMessenger messenger)
    {
        _documents = documents;
        _closed = closed;
        closed.Changed += _ => QueueRefresh();
        _dialogs = dialogs;
        _messenger = messenger;
        documents.Documents.CollectionChanged += OnDocumentsChanged;
        foreach (var document in documents.Documents)
            Watch(document);
        Refresh();
    }

    public ObservableCollection<BreakpointRow> Rows { get; } = [];

    [ObservableProperty] public partial BreakpointRow? Selected { get; set; }

    public bool IsEmpty => Rows.Count == 0;

    // ------------------------------------------------------------- commands

    /// <summary>Double-click / Enter: open the file at the breakpoint's line.</summary>
    [RelayCommand]
    private void GoTo(BreakpointRow? row)
    {
        if (row is not null)
            _messenger.Send(new NavigateToDebugLocationMessage(row.Path, row.Line));
    }

    /// <summary>Delete (the key, the row's menu): remove the breakpoint.</summary>
    [RelayCommand]
    private void Delete(BreakpointRow? row)
    {
        if (row is null)
            return;
        var index = Rows.IndexOf(row);
        if (OpenDocumentFor(row.Path) is { } document)
        {
            if (document.BreakpointAt(row.Line) is not null)
                document.ToggleBreakpoint(row.Line);
        }
        else
        {
            _closed.Remove(row.Path, row.Line);
        }
        Refresh();
        if (Rows.Count > 0)
            Selected = Rows[Math.Clamp(index, 0, Rows.Count - 1)];   // keep the keyboard on the list
    }

    /// <summary>Debug > Delete All Breakpoints (Ctrl+Shift+F9), asking first as Visual Studio does.</summary>
    [RelayCommand]
    private async Task DeleteAll()
    {
        var withBreakpoints = _documents.Documents.Where(d => d.Breakpoints.Count > 0).ToArray();
        if (withBreakpoints.Length == 0 && !_closed.All.Any())
        {
            _messenger.Send(new StatusMessage("There are no breakpoints."));
            return;
        }
        if (!await _dialogs.ConfirmAsync("Delete All Breakpoints", "Do you want to delete all breakpoints?", "Delete"))
            return;
        foreach (var document in withBreakpoints)
            document.ClearBreakpoints();
        _closed.Clear();
    }

    /// <summary>Debug > Disable All Breakpoints / Enable All Breakpoints.</summary>
    [RelayCommand]
    private void SetAllEnabled(bool enabled)
    {
        foreach (var document in _documents.Documents)
            foreach (var breakpoint in document.Breakpoints)
                document.SetBreakpointEnabled(breakpoint.Line, enabled);
        _closed.SetAllEnabled(enabled);
    }

    /// <summary>A row's check box.</summary>
    private void SetEnabled(BreakpointRow row, bool enabled)
    {
        if (OpenDocumentFor(row.Path) is { } document)
            document.SetBreakpointEnabled(row.Line, enabled);
        else
            _closed.SetEnabled(row.Path, row.Line, enabled);
    }

    private OpenDocument? OpenDocumentFor(string path)
        => _documents.Documents.FirstOrDefault(document => string.Equals(document.Path, path, StringComparison.Ordinal));

    /// <summary>The section's button (Visual Studio's "Enable or Disable All Breakpoints"): off if any are on, else on.</summary>
    [RelayCommand]
    private void ToggleAllEnabled() => SetAllEnabled(!Rows.Any(row => row.IsEnabled));

    // ------------------------------------------------------------- keeping the list current

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var document in _watched.Where(document => !_documents.Documents.Contains(document)).ToArray())
        {
            _watched.Remove(document);
            document.BreakpointsChanged -= QueueRefresh;
            document.Document.LineCountChanged -= OnLineCountChanged;
        }
        foreach (var document in _documents.Documents)
            Watch(document);
        QueueRefresh();
    }

    private void Watch(OpenDocument document)
    {
        if (!_watched.Add(document))
            return;
        document.BreakpointsChanged += QueueRefresh;
        document.Document.LineCountChanged += OnLineCountChanged;   // lines added or removed above move breakpoints
    }

    private void OnLineCountChanged(object? sender, EventArgs e) => QueueRefresh();

    /// <summary>Refresh once things settle (typing changes the line count often).</summary>
    private void QueueRefresh()
    {
        if (_refreshPending)
            return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Bring the rows up to date. With the same breakpoints in the same order (a check box, an edit moving them)
    /// the rows are updated in place, so the list keeps its selection and scroll position.
    /// </summary>
    private void Refresh()
    {
        var current = _documents.Documents
            .SelectMany(document => document.Breakpoints.Select(info => (document.Path, Info: info)))
            .Concat(_closed.All.SelectMany(file => file.Breakpoints.Select(info => (file.Path, Info: info))))
            .OrderBy(item => System.IO.Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Info.Line)
            .ToList();

        var sameShape = current.Count == Rows.Count
                        && current.Select(item => item.Path).SequenceEqual(Rows.Select(row => row.Path));
        if (sameShape)
        {
            for (var i = 0; i < current.Count; i++)
                Rows[i].Update(current[i].Info);
            return;
        }

        var selected = Selected is { } row ? (row.Path, row.Line) : default;
        Rows.Clear();
        foreach (var (path, info) in current)
            Rows.Add(new BreakpointRow(path, info, SetEnabled));
        Selected = Rows.FirstOrDefault(r => r.Path == selected.Path && r.Line == selected.Line);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

