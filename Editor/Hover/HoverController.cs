using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.CodeActions;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.Hover;

/// <summary>
/// The hover card (<see cref="HoverCard"/>), the same for C# and AXAML: the diagnostics under the pointer (with
/// a Quick Fix... link) and the symbol's signature and documentation.
///
///   - It appears when the pointer rests over the text itself (not past the end of a line, and not while a mouse
///     button is down, e.g. selecting), after a short pause; the language server (or XAML engine) is asked then.
///   - It's a popup that never takes focus, so Enter and typing stay with the editor; any key, click, scroll or
///     edit closes it, and so does a click outside it (light dismiss).
///   - It stays open while the pointer is over the hovered text or the card itself (so its text can be selected
///     and the link clicked), and closes as soon as the pointer is over neither (VS Code). No hide timer: every
///     pointer move - in the editor, elsewhere in the window, or leaving the card - just checks where it is.
///   - A card that's been clicked in (text selected, a DataTip expanded, its menu opened) is "used": it stays
///     until the pointer moves well away from it, so its menus and a slightly wandering pointer don't close it.
///   - Quick fixes are only listed when asked for (the link, the lightbulb, or Ctrl+.), never on hover.
/// </summary>
public sealed class HoverController
{
    /// <summary>How long the pointer rests before the hover is asked for (VS Code's editor.hover.delay default).
    /// Without it, sweeping across a line would ask the language server about every character on the way.</summary>
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How far (DIPs) outside the card still counts as on it: bridges the gap between the text and the card.</summary>
    private const double CardMargin = 4;

    /// <summary>How far (DIPs) the pointer can stray outside a card that's been used before it closes.</summary>
    private const double StickyMargin = 48;

    private readonly EditorContext _ctx;
    private readonly CodeActionController _codeActions;
    private readonly DataTips _dataTips;
    private readonly DispatcherTimer _showTimer = new() { Interval = ShowDelay };
    private readonly Popup _popup;
    private CancellationTokenSource? _hoverCts;
    private int? _hoverOffset;               // the character the pointer rests on
    private (int Start, int End)? _tipRange; // what the open card is about
    private Control? _card;
    private bool _sticky;        // the card was clicked / expanded: it stays until the pointer moves well away
    private bool _refitting;     // re-opening the popup at a new size, not closing it
    private TopLevel? _watchedWindow; // while a card is open: pointer moves anywhere in the window (see OnWindowPointerMoved)

    /// <param name="popup">The popup to show the card in (EditorPaneView.axaml: placed under the text, light dismiss).</param>
    /// <param name="dataTips">While the debugger is paused, hovering an expression shows its value instead (DataTips).</param>
    public HoverController(EditorContext ctx, CodeActionController codeActions, Popup popup, DataTips dataTips)
    {
        _ctx = ctx;
        _codeActions = codeActions;
        _dataTips = dataTips;
        _popup = popup;
        var editor = ctx.Editor;
        var textView = editor.TextArea.TextView;
        _popup.PlacementTarget = textView;
        // Also closed by light dismiss (a click outside it), not only by Hide.
        _popup.Closed += (_, _) =>
        {
            if (!_refitting)
                Forget();
        };

        // handledEventsToo: the text area marks pointer events handled while selecting.
        editor.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.AddHandler(InputElement.PointerPressedEvent, (_, _) => Hide(resetOffset: true), RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.AddHandler(InputElement.KeyDownEvent, (_, _) => Hide(resetOffset: true), RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.PointerExited += OnPointerExited;
        textView.ScrollOffsetChanged += (_, _) => Hide(resetOffset: true);
        editor.TextChanged += (_, _) => Hide(resetOffset: true);

        _showTimer.Tick += OnShowTimer;

        codeActions.FlyoutOpening += () => Hide(resetOffset: true);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Selecting with the mouse (or any drag): no hover, and none popping up underneath.
        if (e.GetCurrentPoint(_ctx.Editor).Properties is { IsLeftButtonPressed: true } or { IsRightButtonPressed: true } or { IsMiddleButtonPressed: true })
        {
            Hide(resetOffset: true);
            return;
        }

        if (_popup.IsOpen)
        {
            if (KeepsCardOpen(ScreenPoint(e, _ctx.Editor)))
                return;
            Hide();   // moved off the word: close now, and maybe hover what it's over instead (below)
        }

        var offset = CharacterUnderPointer(e.GetPosition(_ctx.Editor.TextArea.TextView));
        if (offset == _hoverOffset)
            return;

        _hoverOffset = offset;
        _showTimer.Stop();
        if (offset is not null)
            _showTimer.Start();
    }

    // The editor also gets PointerExited when the card opens over it or the pointer moves onto the card (it's in
    // another window, or the overlay layer); KeepsCardOpen sorts those out by where the pointer actually is.
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (TopLevel.GetTopLevel(_ctx.Editor) is null)
        {
            _showTimer.Stop();
            _hoverOffset = null;
            if (_popup.IsOpen)
                Hide(resetOffset: true);
            return;
        }
        var point = ScreenPoint(e, _ctx.Editor);

        if (!new Rect(_ctx.Editor.Bounds.Size).Contains(e.GetPosition(_ctx.Editor)))
        {
            _showTimer.Stop();   // really left: don't pop up a card for where it was
            _hoverOffset = null;
        }
        if (_popup.IsOpen && !_sticky && !KeepsCardOpen(point))
            Hide(resetOffset: true);
    }

    /// <summary>
    /// The open card stays while the pointer is on the text it's about or on the card (a used card: near it).
    /// Works from a screen point because the card usually lives in its own popup window.
    /// </summary>
    private bool KeepsCardOpen(PixelPoint screenPoint)
    {
        if (IsNearCard(screenPoint, _sticky ? StickyMargin : CardMargin))
            return true;
        if (_tipRange is not { } range)
            return false;

        var view = _ctx.Editor.TextArea.TextView;
        var inView = view.PointToClient(screenPoint);
        return new Rect(view.Bounds.Size).Contains(inView)
               && CharacterUnderPointer(inView) is { } over
               && over >= range.Start && over < Math.Max(range.End, range.Start + 1);
    }

    private static PixelPoint ScreenPoint(PointerEventArgs e, Visual relativeTo)
        => relativeTo.PointToScreen(e.GetPosition(relativeTo));

    /// <summary>The offset of the character the pointer is over, or null when it isn't over any text.</summary>
    private int? CharacterUnderPointer(Point pointInTextView)
    {
        var view = _ctx.Editor.TextArea.TextView;
        if (_ctx.Active is not { } document || !view.VisualLinesValid)
            return null;
        var point = pointInTextView + view.ScrollOffset;
        if (view.GetVisualLineFromVisualTop(point.Y) is not { } line)
            return null;

        var column = line.GetVisualColumnFloor(point);
        if (column >= line.VisualLength)
            return null;   // past the end of the line
        var left = line.GetVisualPosition(column, VisualYPosition.LineTop).X;
        var right = line.GetVisualPosition(column + 1, VisualYPosition.LineTop).X;
        if (point.X < left || point.X > right)
            return null;

        var offset = line.FirstDocumentLine.Offset + line.GetRelativeOffset(column);
        return offset < document.Document.TextLength ? offset : null;
    }

    private async void OnShowTimer(object? sender, EventArgs e)
    {
        _showTimer.Stop();
        if (_ctx.Active is not { } document || _hoverOffset is not { } offset || offset >= document.Document.TextLength)
            return;

        _hoverCts?.Cancel();
        var cts = _hoverCts = new CancellationTokenSource();

        // Paused in the debugger: the value of the expression under the pointer (Visual Studio's DataTip).
        if (await _dataTips.CreateAsync(document, offset, _ctx.Editor.FontSize, () => Hide(resetOffset: true)) is { } dataTip)
        {
            if (cts.IsCancellationRequested || offset != _hoverOffset || !ReferenceEquals(document, _ctx.Active))
                return;
            ShowCard(dataTip.View, dataTip.Range, dataTip.AnchorOffset);
            // Expanding is using it; and the popup has to grow with it.
            dataTip.Tree?.Rows.CollectionChanged += (_, _) =>
            {
                if (!ReferenceEquals(_card, dataTip.View))
                    return;
                _sticky = true;
                Dispatcher.UIThread.Post(Refit, DispatcherPriority.Background);
            };
            return;
        }
        if (cts.IsCancellationRequested || offset != _hoverOffset)
            return;

        // Diagnostics are local and instant: the ones covering this character.
        var segments = document.Diagnostics.FindSegmentsContaining(offset)
            .Where(segment => offset >= segment.StartOffset && (offset < segment.EndOffset || segment.Length == 0))
            .ToList();
        var diagnostics = segments
            .OrderBy(segment => segment.Severity)
            .Select(segment => new HoverDiagnostic(segment.Severity, segment.Message))
            .DistinctBy(diagnostic => diagnostic.Message)
            .ToList();
        (int Start, int End) range = segments.Count > 0 ? (segments[0].StartOffset, segments[0].EndOffset) : (offset, offset + 1);

        var (signature, documentation, symbolRange) = await GetSymbolHoverAsync(document, offset, cts.Token);
        if (cts.IsCancellationRequested || offset != _hoverOffset || !ReferenceEquals(document, _ctx.Active))
            return;   // the pointer moved on, or the tab changed, while we were waiting

        if (segments.Count == 0 && symbolRange is { } symbol)
            range = symbol;
        var content = new HoverContent(diagnostics, signature, documentation);
        if (!content.IsEmpty)
            Show(content, range, quickFixLine: diagnostics.Count > 0 && _ctx.Languages.ClientFor(document)?.SupportsCodeActions == true
                ? document.Document.GetLineByOffset(offset).LineNumber
                : null);
    }

    /// <summary>The symbol's hover from the XAML engine (AXAML) or the language server, read into one shape.</summary>
    private async System.Threading.Tasks.Task<(string? Signature, string? Documentation, (int, int)? Range)> GetSymbolHoverAsync(
        OpenDocument document, int offset, CancellationToken cancellationToken)
    {
        try
        {
            if (document.LanguageId == LanguageIds.Axaml)
            {
                // On a binding path: the bound view-model property's C# hover (as in a .cs file), when the C#
                // server knows it; otherwise the XAML engine's (which also covers elements and attributes).
                var source = document.Document.Text;
                if (await _ctx.Xaml.GetBindingTargetAsync(document.Path, source, offset) is { } binding
                    && await BindingSymbols.HoverAsync(_ctx.Languages, binding, cancellationToken) is { } bound)
                {
                    var (boundSignature, boundDocs) = bound.IsMarkdown ? HoverContent.FromMarkdown(bound.Text) : HoverContent.FromPlainText(bound.Text);
                    return (boundSignature, boundDocs, null);
                }

                var text = await _ctx.Xaml.GetHoverAsync(document.Path, source, offset);
                if (text is null)
                    return (null, null, null);
                var (signature, documentation) = HoverContent.FromPlainText(text);
                return (signature, documentation, null);
            }

            if (_ctx.Languages.ConnectionFor(document) is not { Client: var client, Sync: var sync })
                return (null, null, null);
            sync.Flush();   // the server has to see what's on screen
            var location = document.Document.GetLocation(offset);
            if (await client.HoverAsync(document.Uri, location.Line - 1, location.Column - 1, cancellationToken) is not { } hover)
                return (null, null, null);

            var (codeSignature, docs) = hover.IsMarkdown ? HoverContent.FromMarkdown(hover.Text) : HoverContent.FromPlainText(hover.Text);
            (int, int)? range = hover.Range is { } r
                ? (document.OffsetOf(r.StartLine, r.StartChar), document.OffsetOf(r.EndLine, r.EndChar))
                : null;
            return (codeSignature, docs, range);
        }
        catch (OperationCanceledException)
        {
            return (null, null, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{document.LanguageId} hover] {ex}");
            return (null, null, null);
        }
    }

    private void Show(HoverContent content, (int Start, int End) range, int? quickFixLine)
    {
        var editor = _ctx.Editor;
        Action? quickFix = quickFixLine is { } line
            ? () =>
            {
                var header = string.Join(Environment.NewLine, content.Diagnostics.Select(diagnostic => diagnostic.Message));
                Hide(resetOffset: true);
                _ = _codeActions.ShowCodeActionsAsync(line, header);
            }
        : null;

        ShowCard(HoverCard.Create(content, editor.FontFamily, Math.Max(editor.FontSize - 1, 10), quickFix), range);
    }

    /// <summary>
    /// Open the popup with a card (the hover card, or a DataTip) under <paramref name="anchorOffset"/> (default: the
    /// start of <paramref name="range"/>).
    /// </summary>
    private void ShowCard(Control card, (int Start, int End) range, int? anchorOffset = null)
    {
        var view = _ctx.Editor.TextArea.TextView;
        var start = _ctx.Active!.Document.GetLocation(anchorOffset ?? range.Start);
        var position = view.GetVisualPosition(new TextViewPosition(start), VisualYPosition.LineBottom) - view.ScrollOffset;

        // Leaving the card (when it's in its own window, the editor and main window don't see these moves):
        // close unless the pointer went back onto the word. A used card closes by distance instead
        // (OnWindowPointerMoved) - its own menus are popups too, and moving onto one leaves the card.
        card.PointerExited += (_, e) =>
        {
            if (ReferenceEquals(_card, card) && !_sticky && !KeepsCardOpen(ScreenPoint(e, card)))
                Hide(resetOffset: true);
        };
        // Clicking in it (an expander, a link, selecting text, its menu) means it's being used.
        card.AddHandler(InputElement.PointerPressedEvent, (_, _) => _sticky = true, RoutingStrategies.Tunnel, handledEventsToo: true);

        _sticky = false;
        _card = card;
        WatchWindow(TopLevel.GetTopLevel(_ctx.Editor));
        _popup.Child = card;
        _popup.PlacementRect = new Rect(position.X, position.Y - 1, 1, 1);
        _popup.IsOpen = true;
        _tipRange = range;
        Dispatcher.UIThread.Post(Refit, DispatcherPriority.Background);
    }

    /// <summary>
    /// The card asks for more (or less) room than its popup has: some platforms don't resize an open popup when its
    /// content grows, so open it again at the new size.
    /// </summary>
    private void Refit()
    {
        if (!_popup.IsOpen || _card is not { } card)
            return;
        card.Measure(Size.Infinity);
        var wanted = card.DesiredSize;
        if (Math.Abs(card.Bounds.Height - wanted.Height) < 1 && Math.Abs(card.Bounds.Width - wanted.Width) < 1)
            return;
        _refitting = true;
        try
        {
            _popup.IsOpen = false;
            _popup.IsOpen = true;
        }
        finally
        {
            _refitting = false;
        }
    }

    /// <summary>
    /// While a card is open, pointer moves anywhere in the window: the editor stops getting them once the pointer is
    /// over the explorer, a panel... (and, with overlay popups, over the card itself).
    /// </summary>
    private void WatchWindow(TopLevel? window)
    {
        if (ReferenceEquals(window, _watchedWindow))
            return;
        _watchedWindow?.RemoveHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved);
        _watchedWindow = window;
        _watchedWindow?.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_popup.IsOpen && sender is Visual window && !KeepsCardOpen(ScreenPoint(e, window)))
            Hide(resetOffset: true);
    }

    /// <summary>The screen point is on the card or within <paramref name="margin"/> DIPs of it.</summary>
    private bool IsNearCard(PixelPoint screenPoint, double margin)
    {
        if (_card is not { } card || TopLevel.GetTopLevel(card) is not { } cardTop)
            return false;
        // Screen pixels: the card is usually in its own popup window.
        var origin = card.PointToScreen(default);
        var scale = cardTop.RenderScaling;
        margin *= scale;
        var rect = new Rect(origin.X - margin, origin.Y - margin,
            card.Bounds.Width * scale + 2 * margin, card.Bounds.Height * scale + 2 * margin);
        return rect.Contains(new Point(screenPoint.X, screenPoint.Y));
    }

    public void Hide(bool resetOffset = false)
    {
        _showTimer.Stop();
        _hoverCts?.Cancel();   // abandons an in-flight hover request (and tells the server)
        if (resetOffset)
            _hoverOffset = null;
        if (_popup.IsOpen)
            _popup.IsOpen = false;   // raises Closed -> Forget
        Forget();
    }

    /// <summary>The card is gone (hidden, or light-dismissed by a click outside it): drop what it was about.</summary>
    private void Forget()
    {
        _tipRange = null;
        _sticky = false;
        _card = null;
        WatchWindow(null);
    }
}
