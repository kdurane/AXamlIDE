using System;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Indentation.CSharp;
using AXamlIDE.Services.Indentation;

namespace AXamlIDE.Editor.Indentation;

/// <summary>
/// Enter in AXAML: the new line goes one level inside its parent element (see <see cref="XamlIndentation"/>).
/// Enter between "&lt;Grid&gt;" and "&lt;/Grid&gt;" also moves the closing tag down a line, leaving the caret indented between.
/// </summary>
public sealed class XamlIndentationStrategy(TextArea textArea) : IIndentationStrategy
{
    public string IndentationString { get; set; } = "    ";

    public void IndentLine(TextDocument document, DocumentLine line)
    {
        var text = document.Text;
        var indent = XamlIndentation.IndentFor(text, line.Offset, IndentationString);
        if (XamlIndentation.IsBetweenEmptyTagPair(text, line.Offset))
            SmartIndent.SplitPair(textArea, line, closingIndent: indent, innerIndent: indent + IndentationString);
        else
            document.Replace(TextUtilities.GetLeadingWhitespace(document, line), indent);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
        using (document.RunUpdate())
            for (var number = beginLine; number <= endLine; number++)
            {
                var line = document.GetLineByNumber(number);
                document.Replace(TextUtilities.GetLeadingWhitespace(document, line),
                    XamlIndentation.IndentFor(document.Text, line.Offset, IndentationString));
            }
    }
}

/// <summary>
/// Enter in C#: AvaloniaEdit's brace-aware <see cref="CSharpIndentationStrategy"/>, plus Enter between "{" and "}"
/// moving the "}" down a line, leaving the caret indented between.
/// </summary>
public sealed class CSharpSmartIndentationStrategy(TextArea textArea) : IIndentationStrategy
{
    private readonly CSharpIndentationStrategy _braces = new();

    public string IndentationString
    {
        get => _braces.IndentationString;
        set => _braces.IndentationString = value;
    }

    public void IndentLine(TextDocument document, DocumentLine line)
    {
        _braces.IndentLine(document, line);

        if (line.PreviousLine is { } previous
            && document.GetText(previous).TrimEnd().EndsWith('{')
            && document.GetText(line).TrimStart().StartsWith('}'))
        {
            var closingIndent = document.GetText(TextUtilities.GetLeadingWhitespace(document, line));
            SmartIndent.SplitPair(textArea, line, closingIndent, closingIndent + IndentationString);
        }
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
        => _braces.IndentLines(document, beginLine, endLine);
}

internal static class SmartIndent
{
    /// <summary>
    /// The line holds the closing half of a pair the caret was just between ("}" or "&lt;/Grid&gt;"): give the caret a line
    /// of its own at <paramref name="innerIndent"/>, and put the closing half on the next line at <paramref name="closingIndent"/>.
    /// </summary>
    public static void SplitPair(TextArea textArea, DocumentLine line, string closingIndent, string innerIndent)
    {
        var document = textArea.Document;
        var newLine = TextUtilities.GetNewLineFromDocument(document, line.LineNumber);
        document.Replace(TextUtilities.GetLeadingWhitespace(document, line), innerIndent + newLine + closingIndent);
        textArea.Caret.Offset = line.Offset + innerIndent.Length;
    }
}
