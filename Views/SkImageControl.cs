using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AXamlIDE.Models;

namespace AXamlIDE.Views;

/// <summary>
/// Shows an image drawn straight onto the Skia canvas, without WriteableBitmap and its copies. Source can be a
/// <see cref="SkBitmapImage"/> (the caller keeps and disposes it), <see cref="RawImageData"/> (raw pixels, used in
/// place) or byte[] (an encoded image). The last two are wrapped on first use and disposed when Source changes.
/// </summary>
public sealed class SkImageControl : Control
{
    public static readonly StyledProperty<object?> SourceProperty =
        AvaloniaProperty.Register<SkImageControl, object?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<SkImageControl, Stretch>(nameof(Stretch), Stretch.Uniform);

    private SkBitmapImage? _resolvedImage;
    private object? _resolvedSource;
    private bool _ownsResolvedImage;

    static SkImageControl()
    {
        AffectsRender<SkImageControl>(SourceProperty, StretchProperty);
        AffectsMeasure<SkImageControl>(SourceProperty, StretchProperty);
    }

    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (ResolveImage() is not { } image)
            return;

        var sourceSize = image.Size;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return;

        var bounds = Bounds.Size;
        Rect destRect;
        switch (Stretch)
        {
            case Stretch.None:
                destRect = new Rect(sourceSize);   // native size, top-left
                break;
            case Stretch.Fill:
                destRect = new Rect(bounds);       // aspect ignored
                break;
            case Stretch.UniformToFill:
            {
                var scale = Math.Max(bounds.Width / sourceSize.Width, bounds.Height / sourceSize.Height);
                destRect = Centered(sourceSize * scale, bounds);
                break;
            }
            default:
            {
                var scale = Math.Min(bounds.Width / sourceSize.Width, bounds.Height / sourceSize.Height);
                destRect = Centered(sourceSize * scale, bounds);
                break;
            }
        }

        image.Draw(context, new Rect(sourceSize), destRect);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ResolveImage() is not { } image)
            return default;

        var sourceSize = image.Size;
        if (Stretch == Stretch.None)
            return sourceSize;

        var scale = Math.Min(availableSize.Width / sourceSize.Width, availableSize.Height / sourceSize.Height);
        return double.IsInfinity(scale) ? sourceSize : sourceSize * scale;   // e.g. inside a ScrollViewer
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ReleaseResolvedImage();
        _resolvedSource = null;   // wrapped again if it comes back
    }

    private SkBitmapImage? ResolveImage()
    {
        if (ReferenceEquals(Source, _resolvedSource))
            return _resolvedImage;

        ReleaseResolvedImage();
        _resolvedSource = Source;
        (_resolvedImage, _ownsResolvedImage) = Source switch
        {
            SkBitmapImage image => (image, false),
            RawImageData raw => (new SkBitmapImage(raw), true),
            byte[] encoded => (new SkBitmapImage(encoded), true),
            _ => ((SkBitmapImage?)null, false)
        };
        return _resolvedImage;
    }

    private void ReleaseResolvedImage()
    {
        if (_ownsResolvedImage)
            _resolvedImage?.Dispose();
        _resolvedImage = null;
        _ownsResolvedImage = false;
    }

    private static Rect Centered(Size size, Size bounds)
        => new(new Point((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2), size);
}
