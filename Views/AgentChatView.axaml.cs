using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Claude tool panel. DataContext: <see cref="AgentChatViewModel"/>.</summary>
public partial class AgentChatView : UserControl
{
    private AgentChatViewModel? _viewModel;

    public AgentChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel: before the text box turns Enter into a new line.
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        // Files dropped anywhere on the panel go with the next message.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.FocusRequested -= FocusPrompt;

        _viewModel = DataContext as AgentChatViewModel;
        if (_viewModel is null)
            return;
        _viewModel.FocusRequested += FocusPrompt;
        _viewModel.EnsureStarted();   // the panel is being shown for the first time
        if (_viewModel.TakePendingFocusRequest())
            FocusPrompt();
    }

    /// <summary>VS Code's chat keys: Enter sends, Shift+Enter is a new line, Escape stops the running turn.</summary>
    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        if (IsPasteGesture(e))
        {
            e.Handled = true;   // the clipboard is read asynchronously; plain text is pasted after (PasteAsync)
            _ = PasteAsync();
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (_viewModel.SendCommand.CanExecute(null))
                    _viewModel.SendCommand.Execute(null);
                break;
            case Key.Escape when _viewModel.IsTurnRunning:
                _viewModel.StopCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private bool IsPasteGesture(KeyEventArgs e)
        => Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.Paste.Any(gesture => gesture.Matches(e)) == true;

    /// <summary>
    /// Ctrl+V in the prompt box, as VS Code's chat does it: copied files and pictures (a screenshot) become chips
    /// above the box; anything else is pasted as text.
    /// </summary>
    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || _viewModel is not { } viewModel)
            return;

        if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
        {
            viewModel.AttachFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
            return;
        }
        if (await clipboard.TryGetBitmapAsync() is { } bitmap)
        {
            using var png = new MemoryStream();
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
            viewModel.AttachPastedImage(png.ToArray());
            return;
        }
        PromptBox.Paste();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files)
            return;
        _viewModel.AttachFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        FocusPrompt();
        e.Handled = true;
    }

    /// <summary>The sign-in page, for when the browser didn't open by itself.</summary>
    private async void OnOpenSignInLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            && TopLevel.GetTopLevel(this) is { } topLevel)
            await topLevel.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>The sign-in page's link, to open in another browser.</summary>
    private async void OnCopySignInLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(url);
    }

    private void FocusPrompt() => Dispatcher.UIThread.Post(() => PromptBox.Focus(), DispatcherPriority.Loaded);
}
