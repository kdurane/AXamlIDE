using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Remote.Protocol.Input;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One AXAML tab's preview, everything that isn't pixels: its designer-host process, the target, the 300ms
/// debounce, the theme wrapper, the zoom chosen for it, and the status message. Raises FrameChanged when there
/// is a new frame. Zoom, fit-to-viewport and image sizing need the control's bounds and render scaling, so
/// those stay in PreviewPaneView.
///
/// Every AXAML tab has its own (made by <see cref="PreviewSessions"/>), so each tab keeps showing its own
/// preview whichever tab is selected, even when it's dragged out to another window. Its host starts when the
/// tab is first shown and is killed when the tab closes (as Avalonia's own Visual Studio and VS Code
/// extensions do). A host compiles every update it is sent and never frees that memory, so a host per file
/// keeps the cost to the file's lifetime. Updates run one at a time and only the newest one counts.
///
/// A file edited for a long time still grows its host (every update keeps another preview window alive in
/// there), so after <see cref="RecycleAfterUpdates"/> updates a fresh host is started alongside, given the same
/// XAML and zoom, and swapped in once it has drawn; the old one shows its picture until then.
/// </summary>
public sealed class PreviewPaneViewModel : ObservableObject, IDisposable
{
    /// <summary>About 3 MB of host memory per update for a small file; more for a big one.</summary>
    private const int RecycleAfterUpdates = 100;
    private static readonly TimeSpan ReplacementFirstFrameTimeout = TimeSpan.FromSeconds(30);

    private readonly XamlLanguageService _xaml;
    private readonly IMessenger _messenger;

    private readonly DispatcherTimer _previewUpdateTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };

    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private int _requestVersion;

    // The designer host and what it was last given.
    private AvaloniaPreviewProcess _process = new();
    private XamlPreviewTarget? _target;
    private string? _sentXaml;
    private int _updates;   // sent to this host since it started
    private bool _isRecycling;
    private bool _isDisposed;

    private string _message = "Starting Avalonia preview...";
    private bool _isMessageVisible = true;
    private string _selectedTheme = "No theme";
    private bool _isPausedForBuild;

    public Task SendInputAsync(InputEventMessageBase message) => _process.SendInputAsync(message);

    public PreviewPaneViewModel(OpenDocument document, XamlLanguageService xaml, IMessenger messenger)
    {
        Document = document;
        _xaml = xaml;
        _messenger = messenger;
        Watch(_process);

        document.Document.Changed += OnDocumentChanged;
        _previewUpdateTimer.Tick += OnPreviewUpdateTimer;
        _ = UpdatePreviewAsync();
    }

    /// <summary>The AXAML file this previews.</summary>
    public OpenDocument Document { get; }

    /// <summary>Raised on the UI thread. Null means "clear the image".</summary>
    public event Action<RawImageData?>? FrameChanged;

    /// <summary>The frame shown now (a view connecting later shows this first). Null when there is none.</summary>
    public RawImageData? CurrentFrame { get; private set; }

    private void RaiseFrameChanged(RawImageData? frame)
    {
        CurrentFrame = frame;
        FrameChanged?.Invoke(frame);
    }

    /// <summary>The zoom chosen for this file ("100%", "137%", "Fit All"...), or null if none was chosen yet.</summary>
    public string? Zoom { get; set; }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsMessageVisible
    {
        get => _isMessageVisible;
        private set => SetProperty(ref _isMessageVisible, value);
    }

    /// <summary>"No theme", "Light" or "Dark". The view copies its ComboBox selection here.</summary>
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
                _ = UpdatePreviewAsync();
        }
    }

    /// <summary>Render scaling the designer host is using (zoom * display scaling).</summary>
    public double Scaling => _process.Scaling;

    public static bool IsPreviewable(OpenDocument? document)
        => document?.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true;

    public async Task SetScalingAsync(double scaling)
    {
        if (_isDisposed)
            return;
        try
        {
            await _process.SetScalingAsync(scaling);   // kept for its start if it isn't running yet
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] Could not change preview zoom: {ex}");
            Message = $"Could not change preview zoom: {ex.Message}";
            IsMessageVisible = true;
        }
    }

    /// <summary>A build started: keep showing the last frame, but stop sending updates until it finishes.</summary>
    public void PauseForBuild()
    {
        if (_isDisposed)
            return;
        _isPausedForBuild = true;
        Message = "Preview paused while building...";
        IsMessageVisible = true;
    }

    /// <summary>The build finished: catch up. A new build of the dll restarts the host (see UpdatePreviewAsync).</summary>
    public void ResumeAfterBuild()
    {
        if (_isDisposed)
            return;
        _isPausedForBuild = false;
        IsMessageVisible = CurrentFrame is null;
        _ = UpdatePreviewAsync();
    }


    /// <summary>The tab closed (or the app is exiting): its designer host goes with it.</summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _previewUpdateTimer.Stop();
        Document.Document.Changed -= OnDocumentChanged;
        _process.Dispose();
    }

    // ------------------------------------------------------------ the logic

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _previewUpdateTimer.Stop();
        _previewUpdateTimer.Start();
    }

    private void OnPreviewUpdateTimer(object? sender, EventArgs e)
    {
        _previewUpdateTimer.Stop();
        _ = UpdatePreviewAsync();
    }

    private string GetPreviewXaml(string xaml) => PreviewThemeVariant.Apply(xaml, SelectedTheme);

    /// <summary>
    /// Send the document to the designer host (starting it first if needed), unless the host already has
    /// this text. One at a time: a request that is no longer the newest by the time it gets its turn (or
    /// after any await) gives up.
    /// </summary>
    private async Task UpdatePreviewAsync()
    {
        if (_isDisposed || _isPausedForBuild)
            return;
        var version = ++_requestVersion;
        bool IsStale() => version != _requestVersion || _isDisposed;

        await _updateGate.WaitAsync();
        try
        {
            if (IsStale())
                return;

            var xaml = GetPreviewXaml(Document.Document.Text);
            if (_process.IsReady && xaml == _sentXaml && _target?.IsCurrent == true)
                return;   // the host already shows this text, from the current build

            Message = "Starting Avalonia preview...";
            IsMessageVisible = CurrentFrame is null;

            var target = await _xaml.GetPreviewTargetAsync(Document.Path);
            if (IsStale())
                return;

            if (!_process.IsReady || target != _target)
            {
                _process.Stop();
                _target = target;
                _sentXaml = null;
                _updates = 0;
                Message = "Connecting to the Avalonia designer host...";
                IsMessageVisible = CurrentFrame is null;
                await _process.StartAsync(target);
                if (IsStale())
                    return;
            }

            await _process.UpdateXamlAsync(target.XamlAssemblyPath, Document.Path, xaml);
            _sentXaml = xaml;
            if (++_updates >= RecycleAfterUpdates)
                _ = RecycleAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] {ex}");
            if (!IsStale())
            {
                _process.Stop();
                _target = null;
                _sentXaml = null;
                RaiseFrameChanged(null);
                Message = $"Preview unavailable: {ex.Message}";
                IsMessageVisible = true;
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    /// <summary>A host's frames and errors count only while it is the host (not a replacement warming up, or one swapped out).</summary>
    private void Watch(AvaloniaPreviewProcess process)
    {
        process.FrameReceived += frame => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_process, process))
                OnFrameReceived(frame);
        });
        process.ErrorChanged += error => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_process, process))
                OnErrorChanged(error);
        });
    }

    /// <summary>
    /// Start a fresh host alongside the old one, give it what the old one was last sent, and swap it in once it
    /// has drawn. If anything goes wrong the old host just carries on, and it's tried again after another half a
    /// recycle's worth of updates (not on every keystroke).
    /// </summary>
    private async Task RecycleAsync()
    {
        if (_isRecycling || _target is not { } target || _sentXaml is not { } xaml)
            return;
        _isRecycling = true;
        var replacement = new AvaloniaPreviewProcess();
        Watch(replacement);
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.FrameReceived += frame =>
        {
            if (frame is not null)
                firstFrame.TrySetResult();
        };

        try
        {
            await replacement.SetScalingAsync(_process.Scaling);
            await replacement.StartAsync(target);
            await replacement.UpdateXamlAsync(target.XamlAssemblyPath, Document.Path, xaml);
            await firstFrame.Task.WaitAsync(ReplacementFirstFrameTimeout);
            if (_isDisposed || !ReferenceEquals(_target, target))
            {
                replacement.Dispose();   // the tab closed, or its host was restarted for another target meanwhile
                return;
            }

            await replacement.SetScalingAsync(_process.Scaling);   // in case the zoom changed while it started
            var old = _process;
            _process = replacement;
            _sentXaml = xaml;
            _updates = 1;
            old.Dispose();
            _ = UpdatePreviewAsync();   // edits made while it started go to the new host
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] Couldn't replace the designer host: {ex}");
            replacement.Dispose();
            _updates = RecycleAfterUpdates / 2;
        }
        finally
        {
            _isRecycling = false;
        }
    }

    private void OnFrameReceived(RawImageData? frame)
    {
        if (_isDisposed)
            return;   // a late frame from a closed tab's host

        RaiseFrameChanged(frame);
        IsMessageVisible = frame is null || _isPausedForBuild;
        if (frame is null && string.IsNullOrEmpty(Message))
            Message = "Waiting for preview frame...";
    }

    /// <summary>
    /// The details go to the Problems tab (they can be long, and need the line); the preview only says there's
    /// an error. A successful update clears the problem.
    /// </summary>
    private void OnErrorChanged(PreviewError? error)
    {
        if (_isDisposed)
            return;
        _messenger.Send(new PreviewProblemMessage(Document, error));
        if (error is null)
            return;

        Message = error.Line is { } line
            ? $"The XAML has an error on line {line}: see the Problems tab."
            : "The XAML has an error: see the Problems tab.";
        IsMessageVisible = true;
    }
}
