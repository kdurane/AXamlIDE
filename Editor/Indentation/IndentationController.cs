using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using AXamlIDE.Models;
using AXamlIDE.Services.Indentation;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.Indentation;

/// <summary>
/// Smart indentation for the shared editor. Enter indents the new line for where it is (inside an element or a
/// block), and typing a line's closing "}" or "&lt;/Tag&gt;" moves it back level with its opening.
/// New indentation uses whatever the file already uses, tabs or spaces, and so does the Tab key.
/// </summary>
public sealed class IndentationController
{
    private readonly EditorContext _ctx;
    private readonly XamlIndentationStrategy _xaml;
    private readonly CSharpSmartIndentationStrategy _csharp;
    private readonly DefaultIndentationStrategy _plain = new(); // other files: keep the line above's indentation

    public IndentationController(EditorContext ctx)
    {
        _ctx = ctx;
        _xaml = new XamlIndentationStrategy(ctx.Editor.TextArea);
        _csharp = new CSharpSmartIndentationStrategy(ctx.Editor.TextArea);

        ctx.Attached += Configure;
        ctx.Editor.TextArea.TextEntered += OnTextEntered;
    }

    private void Configure(OpenDocument? doc)
    {
        var editor = _ctx.Editor;
        if (doc is null)
        {
            editor.TextArea.IndentationStrategy = _plain;
            return;
        }

        editor.Options.ConvertTabsToSpaces = !IndentDetection.UsesTabs(doc.Document.Text);
        var unit = editor.Options.IndentationString;
        _xaml.IndentationString = unit;
        _csharp.IndentationString = unit;

        editor.TextArea.IndentationStrategy = doc.LanguageId switch
        {
            LanguageIds.Axaml => _xaml,
            LanguageIds.CSharp => _csharp,
            _ => _plain,
        };
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_ctx.Active is not { } doc)
            return;

        var document = doc.Document;
        var line = document.GetLineByOffset(_ctx.Editor.CaretOffset);
        var typedSoFar = document.GetText(line.Offset, _ctx.Editor.CaretOffset - line.Offset).TrimStart();

        // Only when the closing part is all that's on the line so far: "}" in C#, "</Grid>" in AXAML
        var closesLine = doc.LanguageId switch
        {
            LanguageIds.CSharp => e.Text == "}" && typedSoFar == "}",
            LanguageIds.Axaml => e.Text == ">" && typedSoFar.StartsWith("</") && typedSoFar.IndexOf('>') == typedSoFar.Length - 1,
            _ => false,
        };
        if (closesLine)
            _ctx.Editor.TextArea.IndentationStrategy?.IndentLine(document, line);
    }
}
