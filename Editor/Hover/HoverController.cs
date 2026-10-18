using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Editor.CodeActions;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.Hover;

/// <summary>
/// Hover tooltip: the pointer rests on an offset, a debounce timer fires, the server is asked.
/// Diagnostics under the pointer are shown too, and for AXAML the XAML engine supplies the text.
/// </summary>
public sealed class HoverController
{
    private readonly EditorContext _ctx;
    private readonly CodeActionController _codeActions;

    // Hover: the pointer rests on an offset, a debounce timer fires, the server is asked.
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _hoverCts;
    private int _hoverOffset = -1;
    private (int Start, int End)? _tipRange; // non-null while our tooltip is open
    private bool _tipOpen;

    public HoverController(EditorContext ctx, CodeActionController codeActions)
    {
        _ctx = ctx;
        _codeActions = codeActions;

        var editor = ctx.Editor;

        // handledEventsToo: the text area marks pointer events handled while selecting.
        editor.AddHandler(InputElement.PointerMovedEvent, OnEditorPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        editor.PointerExited += (_, _) => Hide(resetOffset: true);

        _hoverTimer.Tick += OnHoverTimer;

        // The quick-actions flyout replaces the tooltip.
        codeActions.FlyoutOpening += () => Hide(resetOffset: true);
    }

    // Pointer moves are cheap: just note where it is and restart the debounce. The server
    // is only asked once the pointer has rested for a moment.
    private void OnEditorPointerMoved(object? sender, PointerEventArgs e)
    {
        var editor = _ctx.Editor;
        if (_ctx.Active is not { } doc || editor.GetPositionFromPoint(e.GetPosition(editor)) is not { } pos)
        {
            Hide(resetOffset: true);
            return;
        }

        // Below the last line the editor can report a spot past the end of the text.
        var offset = Math.Min(doc.Document.GetOffset(pos.Location), doc.Document.TextLength);

        // Still over what the open tooltip is about: leave it alone.
        if (_tipRange is { } range && offset >= range.Start && offset <= range.End) return;
        if (offset == _hoverOffset) return;

        _hoverOffset = offset;
        Hide();
        _hoverTimer.Start();
    }

    private static bool IsInDocument(OpenDocument doc, int offset)
        => offset >= 0 && offset <= doc.Document.TextLength;

    private async void OnHoverTimer(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_ctx.Active is not { } doc || _hoverOffset < 0) return;

        var offset = _hoverOffset;
        if (!IsInDocument(doc, offset)) return;   // the text got shorter since the pointer stopped

        // Diagnostics are local and instant; the server's hover text is added if it has any.
        var segments = doc.Diagnostics.FindSegmentsContaining(offset).ToList();
        var diagText = string.Join(Environment.NewLine, segments.Select(d => d.Message));
        (int Start, int End)? range = segments.Count > 0 ? (segments[0].StartOffset, segments[0].EndOffset) : null;

        if (segments.Count > 0 && _ctx.Languages.ClientFor(doc)?.SupportsCodeActions == true)
        {
            var diagnosticHeader = string.Join(
                Environment.NewLine,
                segments.Select(segment => segment.Message).Distinct(StringComparer.Ordinal));
            var lineNumber = doc.Document.GetLineByOffset(offset).LineNumber;
            if (await _codeActions.ShowCodeActionsAsync(lineNumber, _ctx.Editor, diagnosticHeader, showAtPointer: true))
                return;
            if (!ReferenceEquals(doc, _ctx.Active) || offset != _hoverOffset)
                return;
        }

        string? hoverText = null;
        var hoverIsMarkdown = false;

        if (doc.LanguageId == LanguageIds.Axaml)
        {
            var cts = _hoverCts = new CancellationTokenSource();
            try
            {
                hoverText = await _ctx.Xaml.GetHoverAsync(doc.Path, doc.Document.Text, offset);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AXAML hover] {ex}");
            }

            if (cts.IsCancellationRequested || offset != _hoverOffset || !ReferenceEquals(doc, _ctx.Active))
                return;
        }
        else if (_ctx.Languages.ConnectionFor(doc) is { Client: var client, Sync: var sync })
        {
            sync.Flush(); // the server has to see what's on screen

            if (!IsInDocument(doc, offset)) return;   // edited while the quick-actions lookup was awaited
            var cts = _hoverCts = new CancellationTokenSource();
            var loc = doc.Document.GetLocation(offset);

            try
            {
                var hover = await client.HoverAsync(doc.Uri, loc.Line - 1, loc.Column - 1, cts.Token);
                if (hover is not null)
                {
                    hoverText = hover.Text;
                    hoverIsMarkdown = hover.IsMarkdown;
                    if (hover.Range is { } r)
                        range = (doc.OffsetOf(r.StartLine, r.StartChar), doc.OffsetOf(r.EndLine, r.EndChar));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{doc.LanguageId} hover] {ex}");
            }

            // The pointer moved on, or the tab changed, while we were waiting.
            if (cts.IsCancellationRequested || offset != _hoverOffset || !ReferenceEquals(doc, _ctx.Active))
                return;
        }

        ShowTip(diagText, hoverText, hoverIsMarkdown, range ?? (offset, offset));
    }

    private void ShowTip(string diagnostics, string? hover, bool hoverIsMarkdown, (int Start, int End) range)
    {
        var editor = _ctx.Editor;
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };

        if (diagnostics.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = diagnostics,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Salmon
            });

        if (!string.IsNullOrEmpty(hover))
        {
            Control hoverContent;
            if (_ctx.Active?.LanguageId == LanguageIds.Axaml)
            {
                hoverContent = new TextBlock
                {
                    Text = hoverIsMarkdown ? MarkdownContentRenderer.ToPlainText(hover) : hover,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                    Foreground = Brushes.Gainsboro
                };
            }
            else if (hoverIsMarkdown)
            {
                var renderedMarkdown = MarkdownContentRenderer.Render(hover);
                if (renderedMarkdown is Panel { Children.Count: 0 })
                {
                    Debug.WriteLine($"[{_ctx.Active?.LanguageId} hover] Markdown contained no renderable blocks.");
                    hoverContent = new TextBlock
                    {
                        Text = hover,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gainsboro
                    };
                }
                else
                    hoverContent = renderedMarkdown;
            }
            else
                hoverContent = new TextBlock
                {
                    Text = hover,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gainsboro
                };

            panel.Children.Add(hoverContent);
        }

        if (panel.Children.Count == 0) return;

        var background = new SolidColorBrush(Color.Parse("#2b2b2b"));
        var tooltipContent = new Border
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                MaxWidth = 536,
                MaxHeight = 420,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = panel
            }
        };

        ToolTip.SetTip(editor, tooltipContent);
        ToolTip.SetPlacement(editor, PlacementMode.Pointer);
        ToolTip.SetIsOpen(editor, true);

        _tipOpen = true;
        _tipRange = range;
    }

    public void Hide(bool resetOffset = false)
    {
        var editor = _ctx.Editor;

        _hoverTimer.Stop();
        _hoverCts?.Cancel(); // abandons an in-flight hover request (and tells the server)
        if (resetOffset) _hoverOffset = -1;

        if (!_tipOpen) return;

        ToolTip.SetIsOpen(editor, false);
        ToolTip.SetTip(editor, null);
        _tipOpen = false;
        _tipRange = null;
    }
}
