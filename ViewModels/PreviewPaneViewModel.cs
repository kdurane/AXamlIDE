using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Models;
using AXamlIDE.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels
{
    /// <summary>
    /// Everything about the AXAML preview that isn't pixels: the designer-host process, the
    /// target, the 300ms debounce, the theme wrapper, and the status message.
    ///
    /// It is given a document (SetDocument) and a way to resolve the preview target, and it
    /// raises FrameChanged when there is a new bitmap. Zoom, fit-to-viewport and image sizing
    /// need the control's bounds and render scaling, so those stay in PreviewPaneView.
    /// </summary>
    public sealed class PreviewPaneViewModel : ObservableObject, IDisposable
    {
        private const string IdleMessage = "Open an Avalonia AXAML file to start previewing.";

        private readonly Func<string, Task<XamlPreviewTarget>> _getTarget;
        private readonly AvaloniaPreviewProcess _previewProcess = new();

        private readonly DispatcherTimer _previewUpdateTimer =
            new() { Interval = TimeSpan.FromMilliseconds(300) };

        private XamlPreviewTarget? _previewTarget;
        private OpenDocument? _document;
        private TextDocument? _hookedDocument;
        private bool _hasFrame;

        private string _message = IdleMessage;
        private bool _isMessageVisible = true;
        private string _selectedTheme = "No theme";

        public PreviewPaneViewModel(Func<string, Task<XamlPreviewTarget>> getTarget)
        {
            _getTarget = getTarget;

            _previewUpdateTimer.Tick += OnPreviewUpdateTimer;
            _previewProcess.FrameReceived += OnPreviewFrameReceived;
            _previewProcess.ErrorChanged += OnPreviewErrorChanged;
        }

        /// <summary>Raised on the UI thread. Null means "clear the image".</summary>
        public event Action<WriteableBitmap?>? FrameChanged;

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
        /// The one input. Call on every tab change (null = no tab). Non-AXAML documents stop
        /// the preview; AXAML documents start/refresh it and are watched for text changes.
        /// </summary>
        public void SetDocument(OpenDocument? document)
        {
            if (_hookedDocument is not null)
            {
                _hookedDocument.Changed -= OnDocumentChanged;
                _hookedDocument = null;
            }

            _document = document;

            if (!IsPreviewable(document))
            {
                HidePreview();
                return;
            }

            _hookedDocument = document!.Document;
            _hookedDocument.Changed += OnDocumentChanged;
            _ = UpdatePreviewAsync(document);
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
        }

        // ------------------------------------------------------------ the logic
        // Everything below is the old MainWindow code with _active -> _document.

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
            FrameChanged?.Invoke(null);
        }

        private void HidePreview()
        {
            _previewUpdateTimer.Stop();
            ClearFrame();
            _previewProcess.Stop();
            _previewTarget = null;
            Message = IdleMessage;
            IsMessageVisible = true;
        }

        private string GetPreviewXaml(string xaml)
        {
            var theme = SelectedTheme;
            if (theme is not ("Light" or "Dark"))
                return xaml;

            XDocument document;
            try
            {
                document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return xaml;
            }

            if (document.Root is not { } root)
                return xaml;

            if (root.Name.LocalName is "Window" or "Application" or "ThemeVariantScope")
            {
                root.SetAttributeValue("RequestedThemeVariant", theme);
            }
            else
            {
                XNamespace avaloniaNamespace = "https://github.com/avaloniaui";
                var themeScope = new XElement(
                    avaloniaNamespace + "ThemeVariantScope",
                    new XAttribute("RequestedThemeVariant", theme),
                    root);
                root.ReplaceWith(themeScope);
            }

            return document.ToString(SaveOptions.DisableFormatting);
        }

        private async Task UpdatePreviewAsync(OpenDocument document)
        {
            if (!ReferenceEquals(document, _document) || !IsPreviewable(document))
                return;

            Message = "Starting Avalonia preview...";
            IsMessageVisible = !_hasFrame;

            try
            {
                var target = await _getTarget(document.Path);
                if (!ReferenceEquals(document, _document))
                    return;

                if (!_previewProcess.IsRunning || target != _previewTarget)
                {
                    ClearFrame();
                    _previewProcess.Stop();
                    _previewTarget = target;
                    Message = "Connecting to the Avalonia designer host...";
                    IsMessageVisible = true;
                    await _previewProcess.StartAsync(target);
                }

                if (!ReferenceEquals(document, _document))
                    return;

                await _previewProcess.UpdateXamlAsync(
                    target.XamlAssemblyPath,
                    document.Path,
                    GetPreviewXaml(document.Document.Text));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[preview] {ex}");
                if (ReferenceEquals(document, _document))
                {
                    ClearFrame();
                    _previewProcess.Stop();
                    _previewTarget = null;
                    Message = $"Preview unavailable: {ex.Message}";
                    IsMessageVisible = true;
                }
            }
        }

        private void OnPreviewFrameReceived(WriteableBitmap? bitmap)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _hasFrame = bitmap is not null;
                FrameChanged?.Invoke(bitmap);

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
}
