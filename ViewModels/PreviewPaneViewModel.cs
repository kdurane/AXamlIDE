using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Everything about the AXAML preview that isn't pixels: the designer-host process, the
/// target, the 300ms debounce, the theme wrapper, and the status message.
///
/// It follows the selected tab and raises FrameChanged when there is a new frame. Zoom, fit-to-viewport
/// and image sizing need the control's bounds and render scaling, so those stay in PreviewPaneView;
/// the zoom chosen for each file is remembered here (<see cref="ZoomFor"/>).
///
/// The designer host is one process per project (it loads that project's build output). It is kept
/// running while you switch tabs, including to non-AXAML files, and only restarted for another project.
/// Updates run one at a time and only the newest one counts, so quick tab switches can't leave it
/// half-started or showing the wrong file. Each file's last frame is kept, so returning to a tab shows
/// it straight away while the fresh one renders.
/// </summary>
public sealed class PreviewPaneViewModel : ObservableObject, IDisposable
{
    private const string IdleMessage = "Open an Avalonia AXAML file to start previewing.";

    private readonly XamlLanguageService _xaml;
    private readonly AvaloniaPreviewProcess _previewProcess = new();

    private readonly DispatcherTimer _previewUpdateTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };

    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly Dictionary<OpenDocument, WriteableBitmap> _lastFrames = [];
    private readonly Dictionary<OpenDocument, string> _zooms = [];

    private XamlPreviewTarget? _previewTarget;
    private OpenDocument? _document;
    private TextDocument? _hookedDocument;
    private WriteableBitmap? _currentFrame;
    private bool _hasFrame;
    private bool _acceptFrames;     // false between a tab switch and the new file reaching the host
    private int _requestVersion;

    private string _message = IdleMessage;
    private bool _isMessageVisible = true;
    private string _selectedTheme = "No theme";

    public PreviewPaneViewModel(XamlLanguageService xaml, DocumentManager documents)
    {
        _xaml = xaml;
        documents.ActiveChanged += SetDocument;   // follows the front tab
        documents.Closing += Forget;

        _previewUpdateTimer.Tick += OnPreviewUpdateTimer;
        _previewProcess.FrameReceived += OnPreviewFrameReceived;
        _previewProcess.ErrorChanged += OnPreviewErrorChanged;
    }

    /// <summary>Raised on the UI thread. Null means "clear the image".</summary>
    public event Action<WriteableBitmap?>? FrameChanged;

    /// <summary>The frame shown now (a view connecting later shows this first). Null when there is none.</summary>
    public WriteableBitmap? CurrentFrame { get; private set; }

    private void RaiseFrameChanged(WriteableBitmap? frame)
    {
        CurrentFrame = frame;
        FrameChanged?.Invoke(frame);
    }

    /// <summary>Raised when the previewed file changes (the view restores that file's zoom).</summary>
    public event Action? DocumentChanged;

    /// <summary>The AXAML file being previewed (null when the selected tab isn't one).</summary>
    public OpenDocument? Document => IsPreviewable(_document) ? _document : null;

    /// <summary>The zoom chosen for a file ("100%", "137%", "Fit All"...), or null if none was chosen yet.</summary>
    public string? ZoomFor(OpenDocument document) => _zooms.GetValueOrDefault(document);

    public void RememberZoom(OpenDocument document, string zoom) => _zooms[document] = zoom;

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
            if (!SetProperty(ref _selectedTheme, value))
                return;
            if (_document is { } document && IsPreviewable(document))
                _ = UpdatePreviewAsync(document);
        }
    }

    /// <summary>Render scaling the designer host is currently using (zoom * display scaling).</summary>
    public double Scaling => _previewProcess.Scaling;

    public static bool IsPreviewable(OpenDocument? document)
        => document?.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true;

    // ----------------------------------------------------------------- input

    /// <summary>
    /// The one input: the front tab changed (null = no tab). Non-AXAML documents stop
    /// the preview; AXAML documents start/refresh it and are watched for text changes.
    /// </summary>
    private void SetDocument(OpenDocument? document)
    {
        if (ReferenceEquals(document, _document))
            return;

        if (_hookedDocument is not null)
        {
            _hookedDocument.Changed -= OnDocumentChanged;
            _hookedDocument = null;
        }

        // Keep a copy of the frame we're leaving, to show when the user comes back.
        if (_document is { } previous && _hasFrame && _currentFrame is { } frame)
            _lastFrames[previous] = Copy(frame);

        _document = document;
        _acceptFrames = false;
        _previewUpdateTimer.Stop();
        DocumentChanged?.Invoke();

        if (!IsPreviewable(document))
        {
            ShowIdle();   // the designer host keeps running for the next AXAML tab
            return;
        }

        if (_lastFrames.TryGetValue(document!, out var lastFrame))
        {
            _hasFrame = true;
            RaiseFrameChanged(lastFrame);
            IsMessageVisible = false;
        }
        else
        {
            ClearFrame();
        }

        _hookedDocument = document!.Document;
        _hookedDocument.Changed += OnDocumentChanged;
        _ = UpdatePreviewAsync(document);
    }

    private void Forget(OpenDocument document)
    {
        _lastFrames.Remove(document);
        _zooms.Remove(document);
    }

    public async Task SetScalingAsync(double scaling)
    {
        try
        {
            await _previewProcess.SetScalingAsync(scaling);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] Could not change preview zoom: {ex}");
            Message = $"Could not change preview zoom: {ex.Message}";
            IsMessageVisible = true;
        }
    }

    public void Dispose()
    {
        _previewUpdateTimer.Stop();
        if (_hookedDocument is not null)
        {
            _hookedDocument.Changed -= OnDocumentChanged;
            _hookedDocument = null;
        }

        _previewProcess.Dispose();
        _updateGate.Dispose();
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
        if (_document is { } document)
            _ = UpdatePreviewAsync(document);
    }

    private void ClearFrame()
    {
        _hasFrame = false;
        RaiseFrameChanged(null);
    }

    private void ShowIdle()
    {
        ClearFrame();
        Message = IdleMessage;
        IsMessageVisible = true;
    }

    private string GetPreviewXaml(string xaml) => PreviewThemeVariant.Apply(xaml, SelectedTheme);

    /// <summary>
    /// Send the document to the designer host (starting it first if needed). One at a time: a request
    /// that is no longer the newest by the time it gets its turn (or after any await) gives up.
    /// </summary>
    private async Task UpdatePreviewAsync(OpenDocument document)
    {
        var version = ++_requestVersion;
        bool IsStale() => version != _requestVersion || !ReferenceEquals(document, _document);

        await _updateGate.WaitAsync();
        try
        {
            if (IsStale() || !IsPreviewable(document))
                return;

            Message = "Starting Avalonia preview...";
            IsMessageVisible = !_hasFrame;

            var target = await _xaml.GetPreviewTargetAsync(document.Path);
            if (IsStale())
                return;

            if (!_previewProcess.IsReady || target != _previewTarget)
            {
                _previewProcess.Stop();
                _previewTarget = target;
                Message = "Connecting to the Avalonia designer host...";
                IsMessageVisible = !_hasFrame;
                await _previewProcess.StartAsync(target);
                if (IsStale())
                    return;
            }

            _acceptFrames = true;
            await _previewProcess.UpdateXamlAsync(
                target.XamlAssemblyPath,
                document.Path,
                GetPreviewXaml(document.Document.Text));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] {ex}");
            if (!IsStale())
            {
                ClearFrame();
                _previewProcess.Stop();
                _previewTarget = null;
                Message = $"Preview unavailable: {ex.Message}";
                IsMessageVisible = true;
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    /// <summary>A copy of a frame (the designer host reuses one bitmap for every frame).</summary>
    private static WriteableBitmap Copy(WriteableBitmap source)
    {
        var copy = new WriteableBitmap(source.PixelSize, source.Dpi, source.Format, source.AlphaFormat);
        using var from = source.Lock();
        using var to = copy.Lock();
        var rowBytes = Math.Min(from.RowBytes, to.RowBytes);
        var row = new byte[rowBytes];
        for (var y = 0; y < source.PixelSize.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(from.Address, y * from.RowBytes), row, 0, rowBytes);
            Marshal.Copy(row, 0, IntPtr.Add(to.Address, y * to.RowBytes), rowBytes);
        }

        return copy;
    }

    private void OnPreviewFrameReceived(WriteableBitmap? bitmap)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_acceptFrames)
                return;   // a late frame of the file we just left

            _currentFrame = bitmap;
            _hasFrame = bitmap is not null;
            RaiseFrameChanged(bitmap);

            IsMessageVisible = bitmap is null;
            if (bitmap is null && string.IsNullOrEmpty(Message))
                Message = "Waiting for preview frame...";
        });
    }

    private void OnPreviewErrorChanged(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Message = message;
            IsMessageVisible = true;
        });
    }
}
