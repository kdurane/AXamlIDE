using System;
using System.Linq;
using Avalonia;
using AvaloniaEdit.Rendering;

namespace AXamlIDE.Editor.CodeLens;

/// <summary>
/// A code lens makes its line taller (the band above the code), but highlights (selection, current line,
/// occurrences...) should only cover the code. These give the band's height and trim it off a rectangle.
/// </summary>
internal static class CodeLensSpace
{
          /// <summary>How much of the top of this visual line is lens rather than code (0 when it has no lens).</summary>
          public static double Above(VisualLine line)
              => line.Elements.OfType<InlineObjectElement>()
                     .Select(element => element.Element)
                     .OfType<CodeLensControl>()
                     .FirstOrDefault()?.LensHeight ?? 0;

          /// <summary>A highlight rectangle (in text view coordinates) with the lens band cut off its top.</summary>
          public static Rect Trim(TextView textView, Rect rect)
          {
                    var line = textView.GetVisualLineFromVisualTop(rect.Top + textView.VerticalOffset);
                    if (line is null)
                              return rect;
                    var lineTop = line.VisualTop - textView.VerticalOffset;
                    var lens = Above(line);
                    if (lens == 0 || rect.Top > lineTop + 0.5)
                              return rect;   // no lens, or a wrapped second row (the lens is only on the first)
                    return new Rect(rect.X, rect.Y + lens, rect.Width, Math.Max(0, rect.Height - lens));
          }
}

