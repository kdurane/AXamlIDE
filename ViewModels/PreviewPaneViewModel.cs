using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Everything about the AXAML preview that isn't pixels: the designer-host processes, the
/// target, the 300ms debounce, the theme wrapper, and the status message.
///
/// It follows the selected tab and raises FrameChanged when there is a new frame. Zoom, fit-to-viewport
/// and image sizing need the control's bounds and render scaling, so those stay in PreviewPaneView;
/// the zoom chosen for each file is remembered here (<see cref="ZoomFor"/>).
///
/// Each open AXAML file gets its own designer host, started when the file is first previewed and killed
/// when its tab closes (as Avalonia's own Visual Studio and VS Code extensions do). A host compiles every
/// update it is sent and never frees that memory, so a host per file keeps the cost to the file's
/// lifetime. A file's host keeps running while other tabs are in front, so coming back shows its last
/// frame straight away, and its XAML is only sent again if it changed. Updates run one at a time and
/// only the newest one counts, so quick tab switches can't leave a host half-started.
/// </summary>
public sealed class PreviewPaneViewModel : ObservableObject, IDisposable
{
    private const string IdleMessage = "Open an Avalonia AXAML file to start previewing.";

    private readonly XamlLanguageService _xaml;

    private readonly DispatcherTimer _previewUpdateTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };

    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly IMessenger _messenger;
    private readonly Dictionary<OpenDocument, PreviewSession> _sessions = [];
    private readonly Dictionary<OpenDocument, string> _zooms = [];

    private OpenDocument? _document;
    private TextDocument? _hookedDocument;
    private int _requestVersion;

    private string _message = IdleMessage;
    private bool _isMessageVisible = true;
    private string _selectedTheme = "No theme";

    public PreviewPaneViewModel(XamlLanguageService xaml, DocumentManager documents, IMessenger messenger)
    {
        _xaml = xaml;
        _messenger = messenger;
        documents.ActiveChanged += SetDocument;   // follows the front tab
        documents.Closing += Forget;

        _previewUpdateTimer.Tick += OnPreviewUpdateTimer;
    }

    /// <summary>Raised on the UI thread. Null means "clear the image".</summary>
    public event Action<RawImageData?>? FrameChanged;

    /// <summary>The frame shown now (a view connecting later shows this first). Null when there is none.</summary>
    public RawImageData? CurrentFrame { get; private set; }

    private void RaiseFrameChanged(RawImageData? frame)
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

    /// <summary>Render scaling the front file's designer host is using (zoom * display scaling).</summary>
    public double Scaling => FrontSession?.Process.Scaling ?? 1;

    public static bool IsPreviewable(OpenDocument? document)
        => document?.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true;

    private PreviewSession? FrontSession => _document is { } document ? _sessions.GetValueOrDefault(document) : null;

    // ----------------------------------------------------------------- input

    /// <summary>
    /// The one input: the front tab changed (null = no tab). Non-AXAML documents show the idle message;
    /// AXAML documents start/refresh their own host and are watched for text changes.
    /// </summary>
    private void SetDocument(OpenDocument? document)
    {
        if (ReferenceEquals(document, _document))
            return;
        _hookedDocument?.Changed -= OnDocumentChanged;
        _hookedDocument = null;

        _document = document;
        _previewUpdateTimer.Stop();
        if (IsPreviewable(document) && !_sessions.ContainsKey(document!))
            _sessions[document!] = NewSession(document!);   // before DocumentChanged: the view sets its zoom on it
        DocumentChanged?.Invoke();

        if (!IsPreviewable(document))
        {
            ShowIdle();   // the AXAML tabs' hosts keep running for when they come back
            return;
        }

        if (_sessions[document!].LastFrame is { } lastFrame)
        {
            RaiseFrameChanged(lastFrame);
            IsMessageVisible = false;
        }
        else
        {
            RaiseFrameChanged(null);
        }

        _hookedDocument = document!.Document;
        _hookedDocument.Changed += OnDocumentChanged;
        _ = UpdatePreviewAsync(document);
    }

    /// <summary>A tab is closing: its designer host goes with it.</summary>
    private void Forget(OpenDocument document)
    {
        _zooms.Remove(document);
        if (_sessions.Remove(document, out var session))
            session.Dispose();
    }

    public async Task SetScalingAsync(double scaling)
    {
        if (FrontSession is not { } session)
            return;
        try
        {
            await session.Process.SetScalingAsync(scaling);   // kept for its start if it isn't running yet
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
        _hookedDocument?.Changed -= OnDocumentChanged;
        _hookedDocument = null;

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
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

    private void ShowIdle()
    {
        RaiseFrameChanged(null);
        Message = IdleMessage;
        IsMessageVisible = true;
    }

    private string GetPreviewXaml(string xaml) => PreviewThemeVariant.Apply(xaml, SelectedTheme);

    /// <summary>
    /// Send the document to its designer host (starting it first if needed), unless the host already has
    /// this text. One at a time: a request that is no longer the newest by the time it gets its turn (or
    /// after any await) gives up.
    /// </summary>
    private async Task UpdatePreviewAsync(OpenDocument document)
    {
        var version = ++_requestVersion;
        bool IsStale() => version != _requestVersion || !ReferenceEquals(document, _document);

        await _updateGate.WaitAsync();
        PreviewSession? session = null;
        try
        {
            if (IsStale() || !IsPreviewable(document) || !_sessions.TryGetValue(document, out session))
                return;

            var xaml = GetPreviewXaml(document.Document.Text);
            if (session.Process.IsReady && xaml == session.SentXaml)
                return;   // coming back to a tab whose host already shows this text

            Message = "Starting Avalonia preview...";
            IsMessageVisible = session.LastFrame is null;

            var target = await _xaml.GetPreviewTargetAsync(document.Path);
            if (IsStale())
                return;

            if (!session.Process.IsReady || target != session.Target)
            {
                session.Process.Stop();
                session.Target = target;
                session.SentXaml = null;
                Message = "Connecting to the Avalonia designer host...";
                IsMessageVisible = session.LastFrame is null;
                await session.Process.StartAsync(target);
                if (IsStale())
                    return;
            }

            await session.Process.UpdateXamlAsync(target.XamlAssemblyPath, document.Path, xaml);
            session.SentXaml = xaml;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] {ex}");
            if (!IsStale() && session is not null && !session.IsDisposed)
            {
                session.Process.Stop();
                session.Target = null;
                session.SentXaml = null;
                session.LastFrame = null;
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

    private PreviewSession NewSession(OpenDocument document)
    {
        var session = new PreviewSession(document);
        session.Process.FrameReceived += frame => Dispatcher.UIThread.Post(() => OnFrameReceived(session, frame));
        session.Process.ErrorChanged += error => Dispatcher.UIThread.Post(() => OnErrorChanged(session, error));
        return session;
    }

    private void OnFrameReceived(PreviewSession session, RawImageData? frame)
    {
        if (session.IsDisposed)
            return;   // a late frame from a closed tab's host
        session.LastFrame = frame;
        if (!ReferenceEquals(session.Document, _document))
            return;   // kept for when its tab comes back

        RaiseFrameChanged(frame);
        IsMessageVisible = frame is null;
        if (frame is null && string.IsNullOrEmpty(Message))
            Message = "Waiting for preview frame...";
    }

    /// <summary>
    /// The details go to the Problems tab (they can be long, and need the line); the preview only says there's
    /// an error. A successful update clears the problem.
    /// </summary>
    private void OnErrorChanged(PreviewSession session, PreviewError? error)
    {
        if (session.IsDisposed)
            return;
        _messenger.Send(new PreviewProblemMessage(session.Document, error));
        if (error is null || !ReferenceEquals(session.Document, _document))
            return;

        Message = error.Line is { } line
            ? $"The XAML has an error on line {line}: see the Problems tab."
            : "The XAML has an error: see the Problems tab.";
        IsMessageVisible = true;
    }

    /// <summary>One open AXAML file's designer host, the target it runs, and what it was sent and last drew.</summary>
    private sealed class PreviewSession(OpenDocument document) : IDisposable
    {
        public OpenDocument Document { get; } = document;
        public AvaloniaPreviewProcess Process { get; } = new();
        public XamlPreviewTarget? Target { get; set; }
        public string? SentXaml { get; set; }
        public RawImageData? LastFrame { get; set; }
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            Process.Dispose();
        }
    }
}
