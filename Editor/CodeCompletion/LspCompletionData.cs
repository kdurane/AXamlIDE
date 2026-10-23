using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AXamlIDE.Editor.Snippets;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Snippets;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.CodeCompletion;

/// <summary>
/// Adapts one LSP CompletionItem to AvaloniaEdit's completion list.
///
/// Servers leave the expensive parts out of the list and fill them in for one item on request (completionItem/resolve):
/// its documentation, and the edits it makes elsewhere (picking StringBuilder adds "using System.Text;"). The completion
/// controller asks for that as soon as an item is selected (<see cref="Resolve"/>), so it's normally there by the time
/// the item is picked; if not, the item goes in straight away and the rest follows when the server answers.
/// </summary>
public sealed class LspCompletionData(LspCompletionItem item, int editStartOffset, double priority, LspClient client, string? filePath)
    : ICompletionData
{
    private Task<LspCompletionItem>? _resolved;
    private TextBlock? _description;

    public IImage? Image => null;

    /// <summary>What the typed prefix is matched against.</summary>
    public string Text => item.FilterText ?? item.Label;

    /// <summary>What the list shows: the label, then (dimmed) the namespace of a type that isn't imported yet.</summary>
    public object Content
    {
        get
        {
            if (item.LabelDescription is not { Length: > 0 } description)
                return item.Label;
            // A new control each time: the list creates one per row it shows, and a control can only be in one.
            return new TextBlock
            {
                Inlines =
                [
                    new Run(item.Label),
                    new Run("  " + description) { Foreground = ThemeBrushes.Get("CompletionDetailForegroundBrush") }
                ]
            };
        }
    }

    /// <summary>Shown beside the list while the item is selected; it fills in once the item is resolved.</summary>
    public object? Description
    {
        get
        {
            if (_description is not null)
                return _description;
            var text = DescriptionText(_resolved is { IsCompletedSuccessfully: true } done ? done.Result : item);
            if (text is null && !client.SupportsCompletionResolve)
                return null;
            return _description = new TextBlock { Text = text ?? item.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 };
        }
    }

    public double Priority { get; } = priority;

    /// <summary>Ask the server for the rest of the item (once). The controller calls this when the item is selected.</summary>
    public Task<LspCompletionItem> Resolve() => _resolved ??= ResolveAsync();

    private async Task<LspCompletionItem> ResolveAsync()
    {
        try
        {
            var resolved = await client.ResolveCompletionAsync(item);
            if (_description is not null && DescriptionText(resolved) is { } text)
                _description.Text = text;
            return resolved;
        }
        catch (Exception ex) when (ex is LspException or IOException or InvalidOperationException or OperationCanceledException)
        {
            return item;   // the server went away or gave up: just the item as it came
        }
    }

    private static string? DescriptionText(LspCompletionItem completion)
        => (completion.Detail, completion.Documentation) switch
        {
            (null, var documentation) => documentation,
            (var detail, null) => detail,
            var (detail, documentation) => detail == documentation ? detail : detail + Environment.NewLine + Environment.NewLine + documentation
        };

    // ---------------------------------------------------------------- picking it

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var document = textArea.Document;

        // Replace from where the server says the word starts up to the caret, which also covers anything typed since
        // the request went out. completionSegment is the window's own view of that span, so it is the fallback.
        var start = item.Edit is not null && editStartOffset <= completionSegment.EndOffset
            ? editStartOffset
            : completionSegment.Offset;
        var end = Math.Max(start, completionSegment.EndOffset);

        // The server's positions are in the text as it is now, before this edit.
        var before = document.CreateSnapshot();

        if (_resolved is { IsCompletedSuccessfully: true } done)
        {
            using (document.RunUpdate())   // one undo step for the item and its using
            {
                Insert(textArea, done.Result, start, end);
                ApplyAdditionalEdits(document, before, done.Result, start, end);
            }
            RunCommand(textArea, done.Result.Command);
            return;
        }

        Insert(textArea, item, start, end);
        _ = FinishAsync(textArea, before, start, end);
    }

    /// <summary>Picked before the server had filled the item in: add its other edits and run its command once it has.</summary>
    private async Task FinishAsync(TextArea textArea, ITextSource before, int start, int end)
    {
        var resolved = await Resolve();
        if (before.Version is not { } version || !version.BelongsToSameDocumentAs(textArea.Document.Version))
            return;   // the editor shows another document by now

        using (textArea.Document.RunUpdate())
            ApplyAdditionalEdits(textArea.Document, before, resolved, start, end);
        RunCommand(textArea, resolved.Command);
    }

    private static void Insert(TextArea textArea, LspCompletionItem completion, int start, int end)
    {
        var newText = completion.Edit?.NewText ?? completion.InsertText ?? completion.Label;
        textArea.Document.Replace(start, end - start, newText);
        textArea.Caret.Offset = start + newText.Length;
    }

    /// <summary>
    /// The item's edits elsewhere in the file (a using), placed by their positions in <paramref name="before"/> (the text
    /// before the item went in) and moved on past any change since. Any that would touch the item's own text are skipped.
    /// </summary>
    private static void ApplyAdditionalEdits(TextDocument document, ITextSource before, LspCompletionItem completion, int start, int end)
    {
        if (completion.AdditionalEdits is not { Count: > 0 } additional)
            return;

        var edits = new List<(int Start, int End, string Text)>();
        foreach (var edit in additional)
        {
            var editStart = OffsetIn(before, edit.Range.StartLine, edit.Range.StartChar);
            var editEnd = OffsetIn(before, edit.Range.EndLine, edit.Range.EndChar);
            if (editEnd < editStart || (editStart < end && editEnd > start))
                continue;
            edits.Add((
                before.Version!.MoveOffsetTo(document.Version, editStart, AnchorMovementType.BeforeInsertion),
                before.Version!.MoveOffsetTo(document.Version, editEnd, AnchorMovementType.BeforeInsertion),
                edit.NewText));
        }

        // From the end of the text back, so the offsets still to come stay valid. The caret moves along with them.
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            document.Replace(edit.Start, edit.End - edit.Start, edit.Text);
    }

    /// <summary>An LSP position (0-based line and character) as an offset into <paramref name="text"/>, kept within its line.</summary>
    private static int OffsetIn(ITextSource text, int line, int character)
    {
        var lineStart = 0;
        for (var i = 0; i < line; i++)
        {
            var newline = text.IndexOf('\n', lineStart, text.TextLength - lineStart);
            if (newline < 0)
                return text.TextLength;
            lineStart = newline + 1;
        }

        var lineEnd = text.IndexOfAny(['\r', '\n'], lineStart, text.TextLength - lineStart);
        if (lineEnd < 0)
            lineEnd = text.TextLength;
        return Math.Min(lineStart + character, lineEnd);
    }

    // ---------------------------------------------------------------- the item's command

    private void RunCommand(TextArea textArea, JsonElement? command)
    {
        if (RoslynCompletion.ComplexEdit(command) is { } complexEdit)
            ApplyComplexEdit(textArea, complexEdit);
        else if (RoslynCompletion.CommandName(command) is { } name)
            _ = ExecuteAsync(name, RoslynCompletion.CommandArguments(command));
    }

    private async Task ExecuteAsync(string name, JsonElement? arguments)
    {
        try
        {
            await client.ExecuteCommandAsync(name, arguments);
        }
        catch (Exception ex) when (ex is LspException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"[completion] command {name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Roslyn's override / partial-method completion: replace "override " with the whole member, then put the caret
    /// where Roslyn says (inside the body, after the call to base).
    /// </summary>
    private void ApplyComplexEdit(TextArea textArea, RoslynComplexEdit complexEdit)
    {
        var document = textArea.Document;
        var start = OffsetIn(document, complexEdit.Edit.Range.StartLine, complexEdit.Edit.Range.StartChar);
        var end = Math.Max(start, OffsetIn(document, complexEdit.Edit.Range.EndLine, complexEdit.Edit.Range.EndChar));

        if (complexEdit.IsSnippet)
        {
            document.Remove(start, end - start);
            textArea.Caret.Offset = start;
            SnippetInserter.Insert(textArea, new SnippetDefinition("", "", "", complexEdit.Edit.NewText), start, filePath);
            return;
        }

        document.Replace(start, end - start, complexEdit.Edit.NewText);
        textArea.Caret.Offset = complexEdit.CaretOffset >= 0
            ? Math.Min(complexEdit.CaretOffset, document.TextLength)
            : start + complexEdit.Edit.NewText.Length;
    }
}
