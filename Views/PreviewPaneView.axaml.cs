using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AXaxmlIDE.ViewModels;

namespace AXaxmlIDE.Views;

/// <summary>
/// The pixels half of the preview: shows the frame, sizes the image, and owns zoom /
/// fit-to-viewport / Ctrl+wheel, all of which need this control's bounds and render scaling.
/// Bodies are the old MainWindow methods; _previewProcess.Scaling became _viewModel.Scaling.
/// DataContext must be a PreviewPaneViewModel (MainWindow sets it).
/// </summary>
public partial class PreviewPaneView : UserControl
{
    private static readonly double[] PreviewZoomLevels = [12.5, 25, 50, 75, 100, 150, 200, 400, 800];

    private readonly DispatcherTimer _previewFitTimer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    private PreviewPaneViewModel? _viewModel;

    public PreviewPaneView()
    {
        InitializeComponent();

        _previewFitTimer.Tick += OnPreviewFitTimer;
        PreviewZoomComboBox.SelectionChanged += OnPreviewZoomSelectionChanged;
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
            _viewModel.FrameChanged -= OnFrameChanged;

        _viewModel = DataContext as PreviewPaneViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FrameChanged += OnFrameChanged;
            _viewModel.SelectedTheme = GetComboBoxSelection(PreviewThemeComboBox) ?? "No theme";
        }
    }

    // Was the image half of OnPreviewFrameReceived (and the image-clearing lines in
    // HidePreview / UpdatePreviewAsync). The view model has already marshalled to the UI thread.
    private void OnFrameChanged(WriteableBitmap? bitmap)
    {
        PreviewImage.Source = bitmap;

        if (bitmap is null)
        {
            _previewFitTimer.Stop();
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            return;
        }

        var renderScaling = GetPreviewRenderScaling();
        PreviewImage.Width = bitmap.Size.Width / renderScaling;
        PreviewImage.Height = bitmap.Size.Height / renderScaling;

        if (IsFitPreviewZoomSelected())
            QueuePreviewFit();
    }

    // ---------------------------------------------------------------- zoom

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        StepPreviewZoom(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void StepPreviewZoom(int direction)
    {
        if (_viewModel is null)
            return;

        var currentZoom = _viewModel.Scaling / GetPreviewRenderScaling();
        var currentIndex = Array.IndexOf(PreviewZoomLevels,
            PreviewZoomLevels.OrderBy(level => Math.Abs(level / 100 - currentZoom)).First());
        var nextIndex = Math.Clamp(currentIndex + direction, 0, PreviewZoomLevels.Length - 1);
        var text = $"{PreviewZoomLevels[nextIndex]:0.##}%";
        PreviewZoomComboBox.SelectedItem = PreviewZoomComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), text, StringComparison.Ordinal));
    }

    private void OnPreviewZoomSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsFitPreviewZoomSelected())
            QueuePreviewFit();
        else
            _ = UpdateSelectedPreviewZoomAsync();
    }

    private async Task UpdateSelectedPreviewZoomAsync()
    {
        if (_viewModel is null)
            return;

        var option = GetComboBoxSelection(PreviewZoomComboBox);
        double zoom;
        if (option is "Fit All" or "Fit to Width")
        {
            if (PreviewImage.Source is not WriteableBitmap bitmap
                || bitmap.Size.Width <= 0
                || bitmap.Size.Height <= 0
                || PreviewViewport.Bounds.Width <= 0
                || PreviewViewport.Bounds.Height <= 0)
                return;

            var baseWidth = bitmap.Size.Width / _viewModel.Scaling;
            var baseHeight = bitmap.Size.Height / _viewModel.Scaling;
            var widthScale = PreviewViewport.Bounds.Width / baseWidth;
            zoom = option == "Fit to Width"
                ? widthScale
                : Math.Min(widthScale, PreviewViewport.Bounds.Height / baseHeight);
            zoom = Math.Round(Math.Clamp(zoom, 0.125, 8), 2, MidpointRounding.ToEven);
        }
        else if (double.TryParse(option?.TrimEnd('%'),
                     System.Globalization.NumberStyles.Number,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var percentage)
                 && percentage is > 0 and <= 800)
        {
            zoom = percentage / 100;
        }
        else
        {
            return;
        }

        // try/catch + the "Could not change preview zoom" message live in the view model now.
        await _viewModel.SetScalingAsync(zoom * GetPreviewRenderScaling());
    }

    private void OnPreviewViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsFitPreviewZoomSelected())
            QueuePreviewFit();
    }

    private bool IsFitPreviewZoomSelected()
        => GetComboBoxSelection(PreviewZoomComboBox) is "Fit All" or "Fit to Width";

    private void QueuePreviewFit()
    {
        _previewFitTimer.Stop();
        _previewFitTimer.Start();
    }

    private void OnPreviewFitTimer(object? sender, EventArgs e)
    {
        _previewFitTimer.Stop();
        if (IsFitPreviewZoomSelected())
            _ = UpdateSelectedPreviewZoomAsync();
    }

    // --------------------------------------------------------------- theme

    // The view model owns the theme wrapping; this just hands it the ComboBox selection.
    private void OnPreviewThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.SelectedTheme = GetComboBoxSelection(PreviewThemeComboBox) ?? "No theme";
    }

    // ------------------------------------------------------------- helpers

    private static string? GetComboBoxSelection(ComboBox comboBox)
        => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private double GetPreviewRenderScaling()
        => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
}
