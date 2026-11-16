using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AXamlIDE.Editor.Highlighting;

namespace AXamlIDE.Views;

/// <summary>
/// Shows a Markdown string as formatted text (with <see cref="MarkdownRenderer"/>, the Markdown preview's renderer),
/// for the Claude panel's replies. The text streams in a few characters at a time, so it's re-rendered at most
/// every <see cref="Throttle"/>, not on every change. Links to web pages open in the browser.
/// The look comes from Styles/MarkdownStyles.axaml (md-* classes). Code blocks are coloured by the
/// <see cref="CodeHighlighterProperty"/> set on an ancestor (DockViewCache sets it on the Claude panel).
/// </summary>
public sealed class MarkdownText : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    /// <summary>Colours code blocks; set once on a parent view and inherited by every MarkdownText inside it.</summary>
    public static readonly AttachedProperty<CodeHighlighter?> CodeHighlighterProperty =
        AvaloniaProperty.RegisterAttached<MarkdownText, Control, CodeHighlighter?>("CodeHighlighter", inherits: true);

    public static CodeHighlighter? GetCodeHighlighter(Control control) => control.GetValue(CodeHighlighterProperty);
    public static void SetCodeHighlighter(Control control, CodeHighlighter? value) => control.SetValue(CodeHighlighterProperty, value);

    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(120);

    private MarkdownRenderer _renderer;
    private CodeHighlighter? _highlighter;
    private readonly DispatcherTimer _timer = new() { Interval = Throttle };
    private string? _rendered;

    public MarkdownText()
    {
        _renderer = new MarkdownRenderer("", OpenLink);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Render();
        };
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeHighlighterProperty)
        {
            UseHighlighter(change.GetNewValue<CodeHighlighter?>());
            return;
        }
        if (change.Property != MarkdownProperty)
            return;
        if (_rendered is null)
            Render();            // the first text shows straight away
        else if (!_timer.IsEnabled)
            _timer.Start();      // later pieces are gathered up
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_highlighter is not null)
            _highlighter.ThemeChanged += Rerender;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_highlighter is not null)
            _highlighter.ThemeChanged -= Rerender;
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            Render();
        }
    }

    private void UseHighlighter(CodeHighlighter? highlighter)
    {
        if (ReferenceEquals(highlighter, _highlighter))
            return;
        var attached = this.IsAttachedToVisualTree();
        if (attached && _highlighter is not null)
            _highlighter.ThemeChanged -= Rerender;
        _highlighter = highlighter;
        if (attached && _highlighter is not null)
            _highlighter.ThemeChanged += Rerender;
        _renderer = new MarkdownRenderer("", OpenLink, highlighter);
        Rerender();
    }

    /// <summary>Render again even though the text is the same (the colours changed).</summary>
    private void Rerender()
    {
        if (_rendered is null)
            return;
        _rendered = null;
        Render();
    }

    private void Render()
    {
        var markdown = Markdown ?? "";
        if (markdown == _rendered)
            return;
        _rendered = markdown;
        Content = _renderer.Render(markdown);
    }

    private async void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && TopLevel.GetTopLevel(this) is { } topLevel)
            await topLevel.Launcher.LaunchUriAsync(uri);
    }
}
