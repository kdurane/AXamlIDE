using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The pixels half of the preview: shows the frame, sizes the image, and owns zoom /
/// fit-to-viewport / Ctrl+wheel, all of which need this control's bounds and render scaling.
/// DataContext must be the PreviewPaneViewModel. There is one, moved into the selected AXAML tab by DockViewCache.
///
/// Zoom: the drop-down picks a preset (or a fit mode); you can also type a value, and Ctrl+wheel moves it
/// <see cref="WheelStepPercent"/> points per notch. The choice is remembered per file by the view model.
/// </summary>
public partial class PreviewPaneView : UserControl
{
    private const double WheelStepPercent = 5;
    private const double MinZoomPercent = 10;
    private const double MaxZoomPercent = 800;
    private const string DefaultZoom = "100%";

    private readonly DispatcherTimer _previewFitTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Wheel and typed zoom changes are applied after a short pause, so a fast wheel doesn't flood the host.
    private readonly DispatcherTimer _zoomApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private PreviewPaneViewModel? _viewModel;
    private string _zoom = DefaultZoom;     // the current choice: "137%", "Fit All", "Fit to Width"
    private double _wheelRemainder;         // smooth-scrolling touchpads send fractions of a notch
    private bool _settingZoomBox;

    public PreviewPaneView()
    {
        InitializeComponent();

        _previewFitTimer.Tick += OnPreviewFitTimer;
        _zoomApplyTimer.Tick += (_, _) =>
        {
            _zoomApplyTimer.Stop();
            _ = ApplyZoomAsync();
        };
        PreviewZoomComboBox.SelectionChanged += OnPreviewZoomSelectionChanged;
        PreviewZoomComboBox.KeyDown += OnPreviewZoomKeyDown;
        PreviewZoomComboBox.LostFocus += (_, _) => CommitTypedZoom();
        PreviewThemeComboBox.SelectionChanged += OnPreviewThemeSelectionChanged;
        PreviewViewport.SizeChanged += OnPreviewViewportSizeChanged;

        // Ctrl+wheel zooms the preview. handledEventsToo: the ScrollViewer marks wheel
        // events handled before they bubble up to the viewport.
        PreviewViewport.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged,
            RoutingStrategies.Bubble, handledEventsToo: true);

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => _previewFitTimer.Stop();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FrameChanged -= OnFrameChanged;
            _viewModel.DocumentChanged -= OnPreviewedDocumentChanged;
        }

        _viewModel = DataContext as PreviewPaneViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FrameChanged += OnFrameChanged;
            _viewModel.DocumentChanged += OnPreviewedDocumentChanged;
            _viewModel.SelectedTheme = GetComboBoxSelection(PreviewThemeComboBox) ?? "No theme";

            // Connected after the tab switch: catch up with the frame and this file's zoom.
            OnFrameChanged(_viewModel.CurrentFrame);
            OnPreviewedDocumentChanged();
        }
    }

    // A new frame from the designer host, or null to clear. Already on the UI thread.
    // Sized in device pixels / render scaling, so one frame pixel is one screen pixel (Stretch="Fill").
    private void OnFrameChanged(RawImageData? frame)
    {
        PreviewImage.Source = frame;

        if (frame is null)
        {
            _previewFitTimer.Stop();
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            return;
        }

        var renderScaling = GetPreviewRenderScaling();
        PreviewImage.Width = frame.Width / renderScaling;
        PreviewImage.Height = frame.Height / renderScaling;

        if (IsFitZoom(_zoom))
            QueuePreviewFit();
    }

    /// <summary>Another file is previewed: bring back the zoom chosen for it.</summary>
    private void OnPreviewedDocumentChanged()
    {
        if (_viewModel?.Document is not { } document)
            return;

        SetZoom(_viewModel.ZoomFor(document) ?? DefaultZoom, remember: false);
        _zoomApplyTimer.Stop();
        _ = ApplyZoomAsync();
    }

    // ---------------------------------------------------------------- zoom input

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;
        e.Handled = true;

        _wheelRemainder += e.Delta.Y;
        var notches = Math.Truncate(_wheelRemainder);
        if (notches == 0)
            return;
        _wheelRemainder -= notches;

        var percent = Math.Clamp(Math.Round(CurrentZoomPercent() + notches * WheelStepPercent),
            MinZoomPercent, MaxZoomPercent);
        SetZoom(FormatPercent(percent));
        _zoomApplyTimer.Stop();
        _zoomApplyTimer.Start();
    }

    private void OnPreviewZoomSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingZoomBox || GetComboBoxSelection(PreviewZoomComboBox) is not { } choice)
            return;

        SetZoom(choice);
        _zoomApplyTimer.Stop();
        _ = ApplyZoomAsync();
    }

    private void OnPreviewZoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitTypedZoom();
        e.Handled = true;
    }

    /// <summary>A value typed into the zoom box ("137", "137%").</summary>
    private void CommitTypedZoom()
    {
        if (_settingZoomBox || PreviewZoomComboBox.Text is not { } text || text == _zoom)
            return;
        if (!TryParsePercent(text, out var percent))
        {
            SetZoom(_zoom, remember: false);   // put the last good value back
            return;
        }

        SetZoom(FormatPercent(Math.Clamp(percent, MinZoomPercent, MaxZoomPercent)));
        _zoomApplyTimer.Stop();
        _ = ApplyZoomAsync();
    }

    /// <summary>Show a zoom choice in the box (selecting the preset if it is one) and remember it for the file.</summary>
    private void SetZoom(string zoom, bool remember = true)
    {
        _zoom = zoom;
        _settingZoomBox = true;
        try
        {
            PreviewZoomComboBox.SelectedItem = PreviewZoomComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Content?.ToString(), zoom, StringComparison.Ordinal));
            PreviewZoomComboBox.Text = zoom;
        }
        finally
        {
            _settingZoomBox = false;
        }

        if (remember && _viewModel?.Document is { } document)
            _viewModel.RememberZoom(document, zoom);
    }

    // ---------------------------------------------------------------- applying it

    private async Task ApplyZoomAsync()
    {
        if (_viewModel is null)
            return;

        double zoom;
        if (IsFitZoom(_zoom))
        {
            if (PreviewImage.Source is not RawImageData frame
                || PreviewViewport.Bounds.Width <= 0
                || PreviewViewport.Bounds.Height <= 0)
                return;

            var baseWidth = frame.Width / _viewModel.Scaling;
            var baseHeight = frame.Height / _viewModel.Scaling;
            var widthScale = PreviewViewport.Bounds.Width / baseWidth;
            zoom = _zoom == "Fit to Width"
                ? widthScale
                : Math.Min(widthScale, PreviewViewport.Bounds.Height / baseHeight);
            zoom = Math.Round(Math.Clamp(zoom, MinZoomPercent / 100, MaxZoomPercent / 100), 2, MidpointRounding.ToEven);
        }
        else if (TryParsePercent(_zoom, out var percent))
        {
            zoom = percent / 100;
        }
        else
        {
            return;
        }

        // The view model reports failures to the status line.
        await _viewModel.SetScalingAsync(zoom * GetPreviewRenderScaling());
    }

    /// <summary>The zoom actually shown, in percent (for fit modes, what the fit worked out to).</summary>
    private double CurrentZoomPercent()
        => TryParsePercent(_zoom, out var percent)
            ? percent
            : Math.Round((_viewModel?.Scaling ?? 1) / GetPreviewRenderScaling() * 100);

    private void OnPreviewViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsFitZoom(_zoom))
            QueuePreviewFit();
    }

    private void QueuePreviewFit()
    {
        _previewFitTimer.Stop();
        _previewFitTimer.Start();
    }

    private void OnPreviewFitTimer(object? sender, EventArgs e)
    {
        _previewFitTimer.Stop();
        if (IsFitZoom(_zoom))
            _ = ApplyZoomAsync();
    }

    // --------------------------------------------------------------- theme

    // The view model owns the theme wrapping; this just hands it the ComboBox selection.
    private void OnPreviewThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.SelectedTheme = GetComboBoxSelection(PreviewThemeComboBox) ?? "No theme";
    }

    // ------------------------------------------------------------- helpers

    private static bool IsFitZoom(string zoom) => zoom is "Fit All" or "Fit to Width";

    private static string FormatPercent(double percent)
        => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static bool TryParsePercent(string text, out double percent)
        => double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out percent)
           && percent > 0;

    private static string? GetComboBoxSelection(ComboBox comboBox)
        => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private double GetPreviewRenderScaling()
        => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
}
