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

/// <summary>One row of the Breakpoints list: a breakpoint in an open document. Its check box turns it on and off.</summary>
public sealed partial class BreakpointRow(OpenDocument document, BreakpointInfo info) : ObservableObject
{
    private bool _updating;

    public OpenDocument Document { get; } = document;

    [ObservableProperty] public partial int Line { get; private set; } = info.Line;
    [ObservableProperty] public partial bool IsEnabled { get; set; } = info.IsEnabled;

    /// <summary>"when count > 3, hit count 2", or null with no Conditions.</summary>
    [ObservableProperty] public partial string? ConditionText { get; private set; } = ConditionsOf(info);

    public string FileName => Document.Title;

    /// <summary>The full path and line, and the Conditions in full.</summary>
    public string ToolTip => ConditionText is { } conditions ? $"{Document.Path}:{Line}\n{conditions}" : $"{Document.Path}:{Line}";

    /// <summary>The check box: tell the document (which tells the margin and the debugger).</summary>
    partial void OnIsEnabledChanged(bool value)
    {
        if (!_updating)
            Document.SetBreakpointEnabled(Line, value);
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
/// breakpoint in the open documents, by file then line, kept up to date as breakpoints are set, switched and moved
/// by edits. A check box turns one on or off; double-click or Enter goes to it; Delete removes it. It also has
/// the Debug menu's Delete All and Enable / Disable All.
/// </summary>
public sealed partial class BreakpointsViewModel : ObservableObject
{
    private readonly DocumentManager _documents;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private readonly HashSet<OpenDocument> _watched = [];
    private bool _refreshPending;

    public BreakpointsViewModel(DocumentManager documents, IDialogService dialogs, IMessenger messenger)
    {
        _documents = documents;
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
            _messenger.Send(new NavigateToDebugLocationMessage(row.Document.Path, row.Line));
    }

    /// <summary>Delete (the key, the row's menu): remove the breakpoint.</summary>
    [RelayCommand]
    private void Delete(BreakpointRow? row)
    {
        if (row is null || row.Document.BreakpointAt(row.Line) is null)
            return;
        var index = Rows.IndexOf(row);
        row.Document.ToggleBreakpoint(row.Line);
        Refresh();
        if (Rows.Count > 0)
            Selected = Rows[System.Math.Clamp(index, 0, Rows.Count - 1)];   // keep the keyboard on the list
    }

    /// <summary>Debug > Delete All Breakpoints (Ctrl+Shift+F9), asking first as Visual Studio does.</summary>
    [RelayCommand]
    private async Task DeleteAll()
    {
        var withBreakpoints = _documents.Documents.Where(d => d.Breakpoints.Count > 0).ToArray();
        if (withBreakpoints.Length == 0)
        {
            _messenger.Send(new StatusMessage("There are no breakpoints."));
            return;
        }
        if (!await _dialogs.ConfirmAsync("Delete All Breakpoints", "Do you want to delete all breakpoints?", "Delete"))
            return;
        foreach (var document in withBreakpoints)
            document.ClearBreakpoints();
    }

    /// <summary>Debug > Disable All Breakpoints / Enable All Breakpoints.</summary>
    [RelayCommand]
    private void SetAllEnabled(bool enabled)
    {
        foreach (var document in _documents.Documents)
            foreach (var breakpoint in document.Breakpoints)
                document.SetBreakpointEnabled(breakpoint.Line, enabled);
    }

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

    private void OnLineCountChanged(object? sender, System.EventArgs e) => QueueRefresh();

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
            .SelectMany(document => document.Breakpoints.Select(info => (Document: document, Info: info)))
            .OrderBy(item => item.Document.Title, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Document.Path, System.StringComparer.Ordinal)
            .ThenBy(item => item.Info.Line)
            .ToList();

        var sameShape = current.Count == Rows.Count
                        && current.Select(item => item.Document).SequenceEqual(Rows.Select(row => row.Document));
        if (sameShape)
        {
            for (var i = 0; i < current.Count; i++)
                Rows[i].Update(current[i].Info);
            return;
        }

        var selected = Selected is { } row ? (row.Document, row.Line) : default;
        Rows.Clear();
        foreach (var (document, info) in current)
            Rows.Add(new BreakpointRow(document, info));
        Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Document, selected.Document) && r.Line == selected.Line);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

