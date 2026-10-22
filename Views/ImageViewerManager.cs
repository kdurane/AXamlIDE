using System;
using System.Collections.Generic;
using System.IO;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>Opens image files in an image tab (ImageDockable) instead of the text editor.</summary>
public sealed class ImageViewerManager(DockLayoutViewModel docking)
{
          private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp"
    };

          public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

          /// <summary>Opens (or brings back) the image's tab; false when the file isn't an image.</summary>
          public bool TryOpen(string path)
          {
                    if (!IsImage(path))
                              return false;
                    if (!docking.ShowPage(ImageDockable.IdFor(path)))
                              docking.OpenPage(new ImageDockable(path));
                    return true;
          }
}
