using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using AXamlIDE.Models;
using SkiaSharp;

namespace AXamlIDE.Views;

/// <summary>
/// An Avalonia IImage backed by a Skia SKImage, drawn straight onto the Skia canvas (<see cref="SkBitmapDrawOperation"/>).
/// For <see cref="RawImageData"/> the byte array is pinned and used in place, with no copy; it is unpinned when Skia
/// lets go of the pixels.
///
/// Avalonia draws on its render thread, after <see cref="Draw"/> has returned, so each draw operation holds a
/// reference: <see cref="Dispose"/> drops the owner's, and the pixels go once the last operation is disposed too.
/// </summary>
public sealed class SkBitmapImage : IImage, IDisposable
{
    private readonly SKBitmap _bitmap;
    private readonly SKImage _image;
    private int _references = 1;   // the owner's
    private int _disposed;

    public SkBitmapImage(RawImageData raw)
    {
        var info = new SKImageInfo(raw.Width, raw.Height, raw.ColorType, raw.AlphaType);
        var rowBytes = raw.RowBytes > 0 ? raw.RowBytes : info.RowBytes;
        if (raw.Width <= 0 || raw.Height <= 0 || raw.Bytes.Length < (long)rowBytes * raw.Height)
            throw new ArgumentException($"{raw.Width}x{raw.Height} pixels don't fit in {raw.Bytes.Length} bytes.", nameof(raw));

        _bitmap = new SKBitmap();
        var handle = GCHandle.Alloc(raw.Bytes, GCHandleType.Pinned);
        if (!_bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), rowBytes,
                (_, context) => ((GCHandle)context!).Free(), handle))
        {
            handle.Free();
            throw new ArgumentException("Skia couldn't use these pixels.", nameof(raw));
        }

        _bitmap.SetImmutable();   // or SKImage.FromBitmap copies the pixels
        _image = SKImage.FromBitmap(_bitmap);
    }

    public SkBitmapImage(byte[] encoded)
    {
        _bitmap = SKBitmap.Decode(encoded) ?? throw new ArgumentException("Not an image Skia can decode.", nameof(encoded));
        _bitmap.SetImmutable();
        _image = SKImage.FromBitmap(_bitmap);
    }

    public Size Size => new(_bitmap.Width, _bitmap.Height);

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (TryAddReference())
            context.Custom(new SkBitmapDrawOperation(destRect, sourceRect, this, _image));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Release();
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
            return;
        _image.Dispose();
        _bitmap.Dispose();
    }

    private bool TryAddReference()
    {
        while (true)
        {
            var count = Volatile.Read(ref _references);
            if (count == 0)
                return false;   // already gone
            if (Interlocked.CompareExchange(ref _references, count + 1, count) == count)
                return true;
        }
    }
}
