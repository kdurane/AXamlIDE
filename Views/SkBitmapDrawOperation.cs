using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace AXamlIDE.Views;

/// <summary>
/// Draws an SKImage straight onto the Skia canvas (ISkiaSharpApiLeaseFeature), skipping Avalonia's image pipeline
/// and its buffer copies. Mitchell cubic resampling for scaling. Holds a reference on the <see cref="SkBitmapImage"/>
/// until Avalonia disposes the operation, so the pixels outlive the frame being drawn on the render thread.
/// </summary>
public sealed class SkBitmapDrawOperation(Rect destRect, Rect sourceRect, SkBitmapImage owner, SKImage image)
    : ICustomDrawOperation
{
    private readonly Rect _destRect = destRect;
    private readonly Rect _sourceRect = sourceRect;
    private readonly SKImage _image = image;
    private int _disposed;

    public Rect Bounds => _destRect;

    public bool HitTest(Point p) => _destRect.Contains(p);

    public bool Equals(ICustomDrawOperation? other)
        => other is SkBitmapDrawOperation operation
           && ReferenceEquals(operation._image, _image)
           && operation._destRect == _destRect
           && operation._sourceRect == _sourceRect;

    public void Render(ImmediateDrawingContext context)
    {
        if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var leaseFeature))
            return;

        using var lease = leaseFeature.Lease();
        var source = new SKRect((float)_sourceRect.Left, (float)_sourceRect.Top, (float)_sourceRect.Right, (float)_sourceRect.Bottom);
        var destination = new SKRect((float)_destRect.Left, (float)_destRect.Top, (float)_destRect.Right, (float)_destRect.Bottom);
        lease.SkCanvas.DrawImage(_image, source, destination, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            owner.Release();
    }
}
