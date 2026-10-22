using System;
using System.IO;
using Avalonia.Media.Imaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// A document tab showing an image file (Views/ImageView). One tab per file; opening it again brings it back.
/// </summary>
public partial class ImageDockable : PageDockable
{
          public ImageDockable(string path)
          {
                    FilePath = path;
                    Id = IdFor(path);
                    Title = Path.GetFileName(path);

                    try
                    {
                              Image = new Bitmap(path);
                              Info = $"{Image.PixelSize.Width} × {Image.PixelSize.Height}  ·  {FormatSize(new FileInfo(path).Length)}";
                    }
                    catch (Exception ex)   // Skia's decoders throw several exception types for a bad file
                    {
                              Error = $"Couldn't show this image: {ex.Message}";
                    }

          }

          public static string IdFor(string path) => $"image:{path}";

          public string FilePath { get; }
          public Bitmap? Image { get; }
          public string? Info { get; }
          public string? Error { get; }

          private static string FormatSize(long bytes) => bytes switch
          {
                    < 1024 => $"{bytes} B",
                    < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                    _ => $"{bytes / (1024.0 * 1024):0.#} MB"
          };

          public override void Dispose() => Image?.Dispose();
}
