using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AXamlIDE.Views;

/// <summary>
/// Shows a Markdown string as formatted text (with <see cref="MarkdownRenderer"/>, the Markdown preview's renderer),
/// for the Claude panel's replies. The text streams in a few characters at a time, so it's re-rendered at most
/// every <see cref="Throttle"/>, not on every change. Links to web pages open in the browser.
/// The look comes from Styles/MarkdownStyles.axaml (md-* classes).
/// </summary>
public sealed class MarkdownText : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(120);

    private readonly MarkdownRenderer _renderer;
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
        if (change.Property != MarkdownProperty)
            return;
        if (_rendered is null)
            Render();            // the first text shows straight away
        else if (!_timer.IsEnabled)
            _timer.Start();      // later pieces are gathered up
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            Render();
        }
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
