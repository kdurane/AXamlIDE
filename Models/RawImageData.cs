using SkiaSharp;

namespace AXamlIDE.Models;

/// <summary>
/// Pixels and how they are laid out, e.g. a frame from the preview's designer host. Never changed once made,
/// so it can be kept and shown again without copying. RowBytes 0 means tightly packed (Width × bytes per pixel).
/// </summary>
public sealed record RawImageData(
    byte[] Bytes,
    int Width,
    int Height,
    SKColorType ColorType = SKColorType.Rgba8888,
    SKAlphaType AlphaType = SKAlphaType.Premul,
    int RowBytes = 0);
