using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace AXamlIDE.Views;

public static class NuGetConverters
{
    // One bitmap per icon (the service hands out the same byte array for the same icon).
    private static readonly ConditionalWeakTable<byte[], Bitmap?> Bitmaps = [];

    /// <summary>A package icon's bytes as an image; null for none, or for a format that can't be shown (SVG).</summary>
    public static readonly IValueConverter Icon = new FuncValueConverter<byte[]?, Bitmap?>(bytes =>
        bytes is null ? null : Bitmaps.GetValue(bytes, Decode));

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, 64);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
