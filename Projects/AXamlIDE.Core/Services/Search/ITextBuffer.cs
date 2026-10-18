namespace AXamlIDE.Services.Search;

/// <summary>
/// The text of a document that is open in the editor. Replace-in-files edits open documents
/// through this (so the edit is undoable and unsaved) instead of writing their files.
/// </summary>
public interface ITextBuffer
{
    string Text { get; set; }
}
