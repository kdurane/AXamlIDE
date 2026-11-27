using System;
using System.Linq;
using Avalonia.Input;
using AvaloniaEdit.Document;
using AXamlIDE.CompletionEngine.Parsing;
using AXamlIDE.Editor.CodeCompletion;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Editor.SignatureHelp;
using AXamlIDE.Models;
using AXamlIDE.Services.CSharp;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;
using static AXamlIDE.Editor.NameChars;

namespace AXamlIDE.Editor;

/// <summary>
/// Reacts to typed characters: auto-closes XAML tags and C# braces, makes a namespace file-scoped when ';'
/// is typed after it, formats the C# block or statement a '}' or ';' finishes, and decides when to open
/// completion or signature help.
///
/// It owns no popups itself; it just calls the other controllers.
/// </summary>
public sealed class TypingController
{
    private readonly EditorContext _ctx;
    private readonly HoverController _hover;
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signature;

    /// <summary>
    /// Where the caret was left after '/' became "/>", so a '>' typed straight after (typing "/>" in full) isn't a
    /// second one. -1 once the caret has moved on.
    /// </summary>
    private int _selfClosedAt = -1;

    public TypingController(EditorContext ctx, HoverController hover, CompletionController completion, SignatureHelpController signature)
    {
        _ctx = ctx;
        _hover = hover;
        _completion = completion;
        _signature = signature;

        ctx.Editor.TextArea.TextEntering += OnTextEntering;
        ctx.Editor.TextArea.TextEntered += OnTextEntered;
        ctx.Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (ctx.Editor.CaretOffset != _selfClosedAt)
                _selfClosedAt = -1;
        };
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

        var features = _ctx.Settings.Features;
        switch (doc.LanguageId)
        {
            case LanguageIds.Axaml when e.Text == ">" && features.AutoCloseXamlTags:
                TryInsertXamlClosingTag(doc.Document);
                break;
            case LanguageIds.Axaml when e.Text == "/" && features.AutoCloseXamlTags:
                TrySelfCloseXamlTag(doc.Document);
                break;
            case LanguageIds.Axaml when e.Text == "{" && features.AutoCloseBraces && IsInAttributeValue(doc.Document):
                TryInsertClosingBrace(doc.Document);
                break;
            case LanguageIds.CSharp when e.Text == "{" && features.AutoCloseBraces:
                TryInsertClosingBrace(doc.Document);
                break;
            case LanguageIds.CSharp when e.Text == ";" && features.FileScopedNamespaceOnSemicolon:
                TryMakeNamespaceFileScoped(doc.Document);
                break;
        }
        FormatOnType(doc, e.Text);

        var client = _ctx.Languages.ClientFor(doc);   // null when the document has no server connection

        // In a comment nothing opens by itself (Ctrl+Space still does), except the tag list for '<' in a /// comment.
        if (CommentAtCaret(doc) is not CommentKind.None and var comment)
        {
            if (comment == CommentKind.Doc && e.Text == "<" && client is not null)
                _ = _completion.ShowCompletionAsync(doc, client.CompletionTriggerCharacters.Contains("<") ? "<" : null);
            return;
        }

        // With completion-while-typing off, only the characters that start something new (. < = ...) open it.
        var xamlTrigger = doc.LanguageId == LanguageIds.Axaml
            && ((features.CompletionWhileTyping && StartsCompletion(e.Text[0])) || e.Text is "<" or " " or "." or "=" or "{" or ":" or ",");

        if (doc.LanguageId == LanguageIds.Axaml && !_completion.IsOpen
            && (xamlTrigger || client?.CompletionTriggerCharacters.Contains(e.Text) == true))
            _ = _completion.ShowCompletionAsync(doc, triggerChar: e.Text);
        else if (client is not null
                 && client.CompletionTriggerCharacters.Contains(e.Text))
            _ = _completion.ShowCompletionAsync(doc, triggerChar: e.Text);
        else if (client is not null && ShouldAskForCompletion(e.Text[0]))
            _ = _completion.ShowCompletionAsync(doc, triggerChar: null);

        if (client is null)
            return;

        // Parameter info opened by hand (Ctrl+Shift+Space) still follows the typing; it just doesn't open by itself.
        if (!features.ParameterInfoWhileTyping && !_signature.IsVisible)
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

    /// <summary>A name is being typed: open the list, or ask again when the open one is incomplete (the server left items out).</summary>
    private bool ShouldAskForCompletion(char typed)
        => _completion.IsOpen
            ? _completion.IsIncomplete && IsNameChar(typed)
            : _ctx.Settings.Features.CompletionWhileTyping && StartsCompletion(typed);

    /// <summary>
    /// Only a letter opens the list as you type. An underscore on its own doesn't: "_ = ..." and fast typing would
    /// have it pop up on every one, offering every _field. The letter after it does, and "_t" then lists _text, _timer...
    /// (the word being completed starts at the '_'). Ctrl+Space still opens it anywhere.
    /// </summary>
    private static bool StartsCompletion(char typed) => char.IsLetter(typed);

    /// <summary>The comment the caret is in: <c>//</c>, <c>/* */</c> or <c>///</c> in C#, <c>&lt;!-- --&gt;</c> in AXAML.</summary>
    private CommentKind CommentAtCaret(OpenDocument doc)
    {
        var caret = _ctx.Editor.CaretOffset;
        return doc.LanguageId switch
        {
            LanguageIds.CSharp => CSharpComments.KindAt(doc.Document.Text, caret),
            LanguageIds.Axaml or LanguageIds.Xml
                when XmlParser.Parse(doc.Document.Text.AsMemory(0, caret)).State == XmlParser.ParserState.InsideComment
                => CommentKind.Plain,
            _ => CommentKind.None
        };
    }

    /// <summary>'}' or ';' typed in C#: format the block or statement it finished (see LanguageSessionService.FormatOnTypeAsync).</summary>
    private void FormatOnType(OpenDocument doc, string text)
    {
        var features = _ctx.Settings.Features;
        if (doc.LanguageId == LanguageIds.CSharp
            && ((text == "}" && features.FormatBlockOnBrace) || (text == ";" && features.FormatStatementOnSemicolon)))
            _ = _ctx.Languages.FormatOnTypeAsync(doc, _ctx.Editor.CaretOffset, text);
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // Typing the '}' that auto-close already put there: step over it instead of adding a second one
        var editor = _ctx.Editor;
        var caret = editor.CaretOffset;
        if (e.Text == "}" && caret < editor.Document.TextLength && editor.Document.GetCharAt(caret) == '}')
        {
            editor.CaretOffset = caret + 1;
            e.Handled = true;   // the '}' is never inserted, so TextEntered doesn't fire either
            if (_ctx.Active is { } doc)
                FormatOnType(doc, "}");   // still the block being finished, as in Visual Studio
            return;
        }

        // Typing the '>' of "/>" when '/' already added it: drop it (the caret is already past the one there).
        var selfClosed = _selfClosedAt;
        _selfClosedAt = -1;
        if (e.Text == ">" && caret == selfClosed && caret >= 2
            && editor.Document.GetCharAt(caret - 1) == '>' && editor.Document.GetCharAt(caret - 2) == '/')
        {
            e.Handled = true;
            return;
        }

        _completion.HandleTextEntering(e);
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

        // Already closed further down: the '>' was deleted and is being typed again.
        if (XamlTagClosing.HasEndTag(document.Text, caret, tagName))
            return;

        document.Insert(caret, $"</{tagName}>");
        editor.CaretOffset = caret;
    }

    /// <summary>
    /// '/' typed in a start tag (not in an attribute value): add the '>' to make it "/>", as Visual Studio does, with
    /// the caret after it; a '>' typed next is dropped (see <see cref="_selfClosedAt"/>). Typed just before the '>' of
    /// an empty element (&lt;Grid/|&gt;&lt;/Grid&gt;), the end tag goes instead.
    /// </summary>
    private void TrySelfCloseXamlTag(TextDocument document)
    {
        var editor = _ctx.Editor;
        var caret = editor.CaretOffset;   // just after the '/'
        if (caret < 2 || document.GetCharAt(caret - 2) == '<')
            return;   // "</": an end tag

        var parser = XmlParser.Parse(document.Text.AsMemory(0, caret - 1));
        if (parser.IsInClosingTag
            || parser.TagName is not { Length: > 0 } tagName
            || parser.State is not (XmlParser.ParserState.StartElement
                or XmlParser.ParserState.InsideElement
                or XmlParser.ParserState.AfterAttributeValue))
            return;

        if (caret < document.TextLength && document.GetCharAt(caret) == '>')
        {
            if (XamlTagClosing.EmptyEndTagAfter(document.Text, caret, tagName) is { } endTag)
                document.Remove(endTag.Start, endTag.Length);
            editor.CaretOffset = caret + 1;
            _selfClosedAt = caret + 1;
            return;
        }

        document.Insert(caret, ">");
        editor.CaretOffset = caret + 1;
        _selfClosedAt = caret + 1;
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

    /// <summary>The caret is inside an attribute's quotes (where '{' starts a markup extension).</summary>
    private bool IsInAttributeValue(TextDocument document)
        => XmlParser.Parse(document.Text.AsMemory(0, _ctx.Editor.CaretOffset)).State == XmlParser.ParserState.AttributeValue;

}
