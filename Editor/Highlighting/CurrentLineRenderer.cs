using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.CodeLens;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// The current line's band, in place of AvaloniaEdit's own (internal) one, so it doesn't cover a code lens's band
/// above the line. Redrawn on caret moves for free: setting TextView.HighlightedLine redraws the Background layer.
/// TextMateController sets the colours from the theme.
/// </summary>
public sealed class CurrentLineRenderer : IBackgroundRenderer
{
          // AvaloniaEdit's defaults, for themes without "editor.lineHighlightBackground".
          private static readonly IBrush DefaultBackground = new ImmutableSolidColorBrush(Color.FromArgb(22, 20, 220, 224));
          private static readonly IPen DefaultBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(52, 0, 255, 110)));

          /// <summary>The "Highlight current line" setting.</summary>
          public bool IsEnabled { get; set; } = true;
          public IBrush Background { get; set; } = DefaultBackground;
          public IPen? Border { get; set; } = DefaultBorder;

          public KnownLayer Layer => KnownLayer.Background;

          public void SetDefaultColors()
          {
                    Background = DefaultBackground;
                    Border = DefaultBorder;
          }

          public void Draw(TextView textView, DrawingContext drawingContext)
          {
                    if (!IsEnabled || textView.HighlightedLine < 1 || textView.GetVisualLine(textView.HighlightedLine) is not { } visualLine)
                              return;   // off, or the caret's line is scrolled out of view

                    var lens = CodeLensSpace.Above(visualLine);
                    var top = visualLine.VisualTop - textView.VerticalOffset + lens;
                    var builder = new BackgroundGeometryBuilder();
                    builder.AddRectangle(textView, new Rect(0, top, textView.Bounds.Width, visualLine.Height - lens));
                    if (builder.CreateGeometry() is { } geometry)
                              drawingContext.DrawGeometry(Background, Border, geometry);
          }
}
