using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Models;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Fades code the language server says isn't needed: an unused using, an unused private member (diagnostics with
/// the Unnecessary tag, see <see cref="DiagnosticSegment.IsUnnecessary"/>), as Visual Studio, Rider and VS Code do.
///
/// It fades whatever colour the text already has, so it must come after TextMate's colouriser and the semantic
/// one in the text view's LineTransformers: by then each piece of text has its final colour, and this only lowers
/// its alpha. Call <see cref="Refresh"/> when the active document's diagnostics change.
/// </summary>
public sealed class UnnecessaryCodeColorizer : DocumentColorizingTransformer
{
    /// <summary>How much of the colour is left. Visual Studio's faded code is about this.</summary>
    private const double FadedOpacity = 0.5;

    private OpenDocument? _document;
    private bool _anyFaded;

    /// <summary>The document whose diagnostics are drawn (null when nothing is open). Set on tab switch.</summary>
    public OpenDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _anyFaded = HasUnnecessary(value);
        }
    }

    /// <summary>
    /// The document's diagnostics changed: redraw the text if something was or now is faded. A redraw rebuilds the
    /// lines on screen, so it's skipped in the usual case of a file with no unnecessary code.
    /// </summary>
    public void Refresh(TextView textView)
    {
        var anyFaded = HasUnnecessary(_document);
        if (anyFaded || _anyFaded)
            textView.Redraw();
        _anyFaded = anyFaded;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        // The editor's text can briefly be another document's while tabs switch.
        if (_document is null || !ReferenceEquals(CurrentContext.Document, _document.Document))
            return;

        foreach (var segment in _document.Diagnostics.FindOverlappingSegments(line.Offset, line.Length))
        {
            if (!segment.IsUnnecessary)
                continue;
            var start = Math.Max(segment.StartOffset, line.Offset);
            var end = Math.Min(segment.EndOffset, line.EndOffset);
            if (end > start)
                ChangeLinePart(start, end, Fade);
        }
    }

    private static void Fade(VisualLineElement element)
    {
        // Each piece keeps its own colour (keyword blue, type teal...), only fainter.
        if (element.TextRunProperties.ForegroundBrush is not ISolidColorBrush { Color: var color })
            return;
        var alpha = (byte)(color.A * FadedOpacity);
        element.TextRunProperties.SetForegroundBrush(
            new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
    }

    private static bool HasUnnecessary(OpenDocument? document)
    {
        if (document is null)
            return false;
        foreach (var segment in document.Diagnostics)
            if (segment.IsUnnecessary)
                return true;
        return false;
    }
}
