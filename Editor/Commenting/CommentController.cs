using AXamlIDE.Services.Commenting;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.Commenting;

/// <summary>
/// Edit > Toggle Comment (Ctrl+/), Comment Selection and Uncomment Selection. The work is done by the
/// commenters in AXamlIDE.Core (LineCommenter for C#, XmlCommenter for AXAML); this applies their edits
/// to the editor as one undo step.
/// </summary>
public sealed class CommentController(EditorContext ctx)
{
    public void Run(CommentMode mode)
    {
        if (ctx.Active is not { } active)
            return;

        var editor = ctx.Editor;
        var document = editor.Document;
        var selection = editor.TextArea.Selection;
        var (start, end) = selection.IsEmpty
            ? (editor.CaretOffset, editor.CaretOffset)
            : (selection.SurroundingSegment.Offset, selection.SurroundingSegment.EndOffset);

        var edits = active.LanguageId switch
        {
            LanguageIds.CSharp => LineCommenter.Apply(document.Text, start, end, mode),
            LanguageIds.Axaml or LanguageIds.Xml => XmlCommenter.Apply(document.Text, start, end, mode),
            _ => null
        };
        if (edits is null)
        {
            ctx.Status("This kind of file has no comments.");
            return;
        }

        using (document.RunUpdate())   // one undo step
            foreach (var edit in edits)   // listed from the end of the text, so earlier offsets stay valid
                document.Replace(edit.Offset, edit.Length, edit.Replacement);
    }
}
