using System;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AXamlIDE.Services.Snippets;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Snippets;

/// <summary>A snippet in the completion list: its prefix, with a snippet icon; picking it inserts the snippet.</summary>
public sealed class SnippetCompletionData(SnippetDefinition snippet, string? filePath) : ICompletionData
{
    // https://pictogrammers.com/library/mdi/icon/code-braces-box/ (VS shows snippets with a similar icon)
    private static readonly Geometry IconGeometry = StreamGeometry.Parse(
        "M6 3a3 3 0 0 0-3 3v12a3 3 0 0 0 3 3h12a3 3 0 0 0 3-3V6a3 3 0 0 0-3-3zm1 4h2v2H8v1a1 1 0 0 1-1 1 1 1 0 0 1 1 1v1h1v2H7a2 2 0 0 1-2-2v-1H4v-2h1V9a2 2 0 0 1 2-2m8 0h2a2 2 0 0 1 2 2v1h1v2h-1v1a2 2 0 0 1-2 2h-2v-2h1v-1a1 1 0 0 1 1-1 1 1 0 0 1-1-1V9h-1z");

    public IImage? Image => new DrawingImage(new GeometryDrawing
    {
        Geometry = IconGeometry,
        Brush = ThemeBrushes.Get("ThemeForegroundBrush") ?? Brushes.Gray
    });

    public string Text => snippet.Prefix;

    public object Content => snippet.Prefix;

    public object? Description =>
        $"{snippet.Name} (snippet)" + (snippet.Description.Length > 0 ? Environment.NewLine + snippet.Description : "")
        + Environment.NewLine + Environment.NewLine + SnippetBody.Preview(snippet.Body);

    /// <summary>Below the language server's items: a snippet is only picked first when its prefix is typed in full.</summary>
    public double Priority => -1_000_000;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var start = completionSegment.Offset;
        // An AXAML element snippet brings its own '<'.
        if (snippet.Body.StartsWith('<') && start > 0 && textArea.Document.GetCharAt(start - 1) == '<')
            start--;
        textArea.Caret.Offset = completionSegment.EndOffset;
        SnippetInserter.Insert(textArea, snippet, start, filePath);
    }
}
