using System;
using System.Linq;
using Avalonia.Input;
using AvaloniaEdit.Document;
using AXamlIDE.CompletionEngine.Parsing;
using AXamlIDE.Editor.CodeCompletion;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Editor.SignatureHelp;
using AXamlIDE.Services.CSharp;
using AXamlIDE.Services.Lsp;
using static AXamlIDE.Editor.NameChars;

namespace AXamlIDE.Editor;

/// <summary>
/// Reacts to typed characters: auto-closes XAML tags and C# braces, makes a namespace file-scoped when ';'
/// is typed after it, and decides when to open completion or signature help.
///
/// It owns no popups itself; it just calls the other controllers.
/// </summary>
public sealed class TypingController
{
    private readonly EditorContext _ctx;
    private readonly HoverController _hover;
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signature;

    public TypingController(EditorContext ctx, HoverController hover, CompletionController completion, SignatureHelpController signature)
    {
        _ctx = ctx;
        _hover = hover;
        _completion = completion;
        _signature = signature;

        ctx.Editor.TextArea.TextEntering += (_, e) => _completion.HandleTextEntering(e);
        ctx.Editor.TextArea.TextEntered += OnTextEntered;
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        _hover.Hide(resetOffset: true);

        if (e.Text == ")")
        {
            _signature.Close();
            return;
        }

        if (_ctx.Active is not { } doc || string.IsNullOrEmpty(e.Text))
            return;

        switch (doc.LanguageId)
        {
            case LanguageIds.Axaml when e.Text == ">":
                TryInsertXamlClosingTag(doc.Document);
                break;
            case LanguageIds.CSharp when e.Text == "{":
                TryInsertClosingBrace(doc.Document);
                break;
            case LanguageIds.CSharp when e.Text == ";":
                TryMakeNamespaceFileScoped(doc.Document);
                break;
        }

        var client = _ctx.Languages.ClientFor(doc);   // null when the document has no server connection
        var xamlTrigger = doc.LanguageId == LanguageIds.Axaml
            && (IsNameStart(e.Text[0]) || e.Text is "<" or " " or "." or "=" or "{" or ":" or ",");

        if (doc.LanguageId == LanguageIds.Axaml && !_completion.IsOpen
            && (xamlTrigger || client?.CompletionTriggerCharacters.Contains(e.Text) == true))
            _ = _completion.ShowCompletionAsync(doc, triggerChar: e.Text);
        else if (client is not null
                 && client.CompletionTriggerCharacters.Contains(e.Text))
            _ = _completion.ShowCompletionAsync(doc, triggerChar: e.Text);
        else if (client is not null
                 && !_completion.IsOpen && IsNameStart(e.Text[0]))
            _ = _completion.ShowCompletionAsync(doc, triggerChar: null);

        if (client is null)
            return;

        if (client.SignatureHelpTriggerCharacters.Contains(e.Text))
        {
            _ = _signature.RequestAsync(doc, e.Text);
        }
        else if (client.SignatureHelpRetriggerCharacters.Contains(e.Text))
        {
            _ = _signature.RequestAsync(doc, e.Text, isRetrigger: true);
        }
        else switch (e.Text)
            {
                case "(":
                    _ = _signature.RequestAsync(doc, triggerCharacter: null);
                    break;
                case "," when _signature.IsVisible:
                    _ = _signature.RequestAsync(doc, e.Text, isRetrigger: true);
                    break;
                default:
                    {
                        if (_signature.IsVisible)
                        {
                            _ = _signature.RequestAsync(doc, triggerCharacter: null, isRetrigger: true);
                        }

                        break;
                    }
            }
    }

    private void TryInsertXamlClosingTag(TextDocument document)
    {
        var editor = _ctx.Editor;
        var caret = editor.CaretOffset;
        if (caret < 2 || document.GetCharAt(caret - 1) != '>')
            return;

        var textBeforeDelimiter = document.Text.AsMemory(0, caret - 1);
        var parser = XmlParser.Parse(textBeforeDelimiter);
        if (parser.IsInClosingTag
            || parser.TagName is not { Length: > 0 } tagName
            || parser.State is not (XmlParser.ParserState.StartElement
                or XmlParser.ParserState.InsideElement
                or XmlParser.ParserState.StartAttribute
                or XmlParser.ParserState.BeforeAttributeValue
                or XmlParser.ParserState.AttributeValue
                or XmlParser.ParserState.AfterAttributeValue))
            return;

        var charBeforeEnd = caret - 2;
        while (charBeforeEnd >= 0 && char.IsWhiteSpace(document.GetCharAt(charBeforeEnd)))
            charBeforeEnd--;
        if (charBeforeEnd >= 0 && document.GetCharAt(charBeforeEnd) == '/')
            return;

        var closingTag = $"</{tagName}>";
        var afterEnd = caret;
        while (afterEnd < document.TextLength && char.IsWhiteSpace(document.GetCharAt(afterEnd)))
            afterEnd++;
        if (document.Text.AsSpan(afterEnd).StartsWith(closingTag, StringComparison.Ordinal))
            return;

        document.Insert(caret, closingTag);
        editor.CaretOffset = caret;
    }

    /// <summary>
    /// ';' typed after "namespace X" (with its block below): make it file-scoped, removing the block's braces and
    /// one level of indentation, as one undoable change.
    /// </summary>
    private void TryMakeNamespaceFileScoped(TextDocument document)
    {
        var semicolon = _ctx.Editor.CaretOffset - 1;
        if (FileScopedNamespace.ConvertAfterSemicolon(document.Text, semicolon) is not { } edits)
            return;

        using (document.RunUpdate())   // one undo step
            foreach (var edit in edits)   // listed from the end of the text, so earlier offsets stay valid
                document.Replace(edit.Offset, edit.Length, edit.Replacement);
        _ctx.Editor.CaretOffset = semicolon + 1;
        _ctx.Status("Made the namespace file-scoped (Ctrl+Z to undo).");
    }

    private void TryInsertClosingBrace(TextDocument document)
    {
        var editor = _ctx.Editor;
        var caret = editor.CaretOffset;
        if (caret < document.TextLength && document.GetCharAt(caret) == '}')
            return;

        document.Insert(caret, "}");
        editor.CaretOffset = caret;
    }
}
