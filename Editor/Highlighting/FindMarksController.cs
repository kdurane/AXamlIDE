using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit.Search;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Where the find panel's matches are while it's open (Ctrl+F), for the overview ruler (VS Code marks them there
/// too). AvaloniaEdit keeps its own results private, so they're found again here with its public search, using
/// the panel's text and options (case, whole word, regex): the same matches it highlights. Found again when those
/// change, when the panel opens, and a moment after the text changes; cleared when the panel closes.
/// </summary>
public sealed class FindMarksController
{
    private const int MaxMatches = 10_000;   // past this the ruler is solid anyway
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(200);

    private readonly EditorContext _ctx;
    private readonly DispatcherTimer _timer;
    private SearchPanel? _panel;

    public FindMarksController(EditorContext ctx)
    {
        _ctx = ctx;
        _timer = new DispatcherTimer { Interval = TypingDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Refresh();
        };

        // AvaloniaEdit makes its find panel when the editor gets its template, after this: follow it when it appears.
        HookPanel();
        ctx.Editor.TemplateApplied += (_, _) => HookPanel();
        ctx.Attached += _ =>
        {
            HookPanel();
            Refresh();
        };
        ctx.TextChanged += _ =>
        {
            if (Offsets.Count == 0 && !IsOpen)
                return;
            _timer.Stop();
            _timer.Start();
        };
    }

    /// <summary>The start offset of each match, in order; empty while the panel is closed.</summary>
    public IReadOnlyList<int> Offsets { get; private set; } = [];

    public event Action? Changed;

    private void HookPanel()
    {
        if (_ctx.Editor.SearchPanel is not { } panel || ReferenceEquals(panel, _panel))
            return;
        if (_panel is { } old)
        {
            old.PropertyChanged -= OnPanelPropertyChanged;
            old.AttachedToVisualTree -= OnPanelShownOrHidden;
            old.DetachedFromVisualTree -= OnPanelShownOrHidden;
        }
        _panel = panel;
        panel.PropertyChanged += OnPanelPropertyChanged;
        // The panel is in the tree only while open (Open adds it to the text area, Close takes it out).
        panel.AttachedToVisualTree += OnPanelShownOrHidden;
        panel.DetachedFromVisualTree += OnPanelShownOrHidden;
    }

    private void OnPanelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SearchPanel.SearchPatternProperty || e.Property == SearchPanel.MatchCaseProperty
            || e.Property == SearchPanel.WholeWordsProperty || e.Property == SearchPanel.UseRegexProperty)
            Refresh();
    }

    private void OnPanelShownOrHidden(object? sender, VisualTreeAttachmentEventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private bool IsOpen => _ctx.Editor.SearchPanel is { IsClosed: false } panel && TopLevel.GetTopLevel(panel) is not null;

    private void Refresh()
    {
        var offsets = Find();
        if (offsets.Count == 0 && Offsets.Count == 0)
            return;
        Offsets = offsets;
        Changed?.Invoke();
    }

    private IReadOnlyList<int> Find()
    {
        if (!IsOpen || _ctx.Editor.SearchPanel is not { } panel || string.IsNullOrEmpty(panel.SearchPattern)
            || _ctx.Active is not { } document)
            return [];
        try
        {
            var strategy = SearchStrategyFactory.Create(panel.SearchPattern, !panel.MatchCase, panel.WholeWords,
                panel.UseRegex ? SearchMode.RegEx : SearchMode.Normal);
            return strategy.FindAll(document.Document, 0, document.Document.TextLength)
                .Take(MaxMatches)
                .Select(match => match.Offset)
                .ToList();
        }
        catch (SearchPatternException)
        {
            return [];   // a regex still being typed
        }
    }
}
