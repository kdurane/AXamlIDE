using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Views;

/// <summary>
/// The preview of a Markdown tab (made by DockViewCache for .md files). Links open as VS Code's preview does: web
/// links in the browser, "#heading" scrolls to the heading, and other files open in the IDE.
/// </summary>
public partial class MarkdownPreviewView : UserControl
{
    private readonly OpenDocument _document = null!;
    private readonly IMessenger _messenger = null!;
    private readonly MarkdownRenderer _renderer = null!;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>For the XAML designer only.</summary>
    public MarkdownPreviewView()
    {
        InitializeComponent();
    }

    public MarkdownPreviewView(OpenDocument document, IMessenger messenger)
    {
        InitializeComponent();
        _document = document;
        _messenger = messenger;
        _renderer = new MarkdownRenderer(Path.GetDirectoryName(document.Path) ?? "", OpenLink);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Render();
        };
        document.Document.TextChanged += OnTextChanged;
        Render();
    }

    /// <summary>The tab closed: stop following its text.</summary>
    public void Shutdown()
    {
        _debounce.Stop();
        _document.Document.TextChanged -= OnTextChanged;
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Render()
    {
        var offset = Scroller.Offset;   // keep the reading place while typing
        Body.Content = _renderer.Render(_document.Document.Text);
        Dispatcher.UIThread.Post(() => Scroller.Offset = offset, DispatcherPriority.Loaded);
    }

    private async void OpenLink(string url)
    {
        if (url.StartsWith('#'))
        {
            if (_renderer.Anchors.TryGetValue(url[1..], out var heading) && heading.TranslatePoint(new Point(0, 0), Body) is { } at)
                Scroller.Offset = new Vector(Scroller.Offset.X, at.Y);
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            if (TopLevel.GetTopLevel(this) is { } topLevel)
                await topLevel.Launcher.LaunchUriAsync(uri);
            return;
        }

        // A file beside this one (README links to docs/setup.md): open it in the IDE.
        var relative = Uri.UnescapeDataString(url.Split('#', '?')[0]);
        if (relative.Length == 0)
            return;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_document.Path) ?? "", relative));
        if (File.Exists(path))
            _messenger.Send(new NavigateToSearchResultMessage(new WorkspaceSearchResult(path, 1, 0, ""), ""));
        else
            _messenger.Send(new StatusMessage($"'{relative}' wasn't found next to {_document.Title}."));
    }
}
