using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Helpers;
using AXamlIDE.Lsp;
using AXamlIDE.Models;

namespace AXamlIDE.EditorFeatures
{
    /// <summary>
    /// Applies LSP workspace edits (rename, code actions, server-initiated edits).
    /// Bodies are the old ApplyWorkspaceEdits / ApplyServerWorkspaceEditAsync from MainWindow.
    /// </summary>
    public sealed class WorkspaceEditService(EditorContext ctx)
    {
        public void Apply(Dictionary<string, List<(LspRange Range, string NewText)>> changes)
        {
            var prepared = new List<(OpenDocument Document, List<(int Start, int End, string Text)> Edits)>();
            foreach (var (path, textEdits) in changes)
            {
                var document = ctx.ViewModel.OpenDocument(path);
                if (document.IsReadOnly)
                    throw new InvalidOperationException($"Cannot apply an edit to read-only document '{path}'.");
                var offsets = textEdits.Select(edit =>
                {
                    var start = document.OffsetOf(edit.Range.StartLine, edit.Range.StartChar);
                    var end = document.OffsetOf(edit.Range.EndLine, edit.Range.EndChar);
                    return end < start ? throw new InvalidOperationException($"Rename returned an invalid range for '{path}'.") : (Start: start, End: end, Text: edit.NewText);
                }).OrderByDescending(edit => edit.Start).ToList();

                for (var index = 1; index < offsets.Count; index++)
                    if (offsets[index - 1].Start < offsets[index].End)
                        throw new InvalidOperationException($"Rename returned overlapping edits for '{path}'.");

                prepared.Add((document, offsets));
            }

            foreach (var (document, edits) in prepared)
                foreach (var edit in edits)
                    document.Document.Replace(edit.Start, edit.End - edit.Start, edit.Text);

            if (prepared.FirstOrDefault(item => ReferenceEquals(item.Document, ctx.Active)) is not
                { Document: { } activeDocument, Edits.Count: > 0 } activeEdit) return;
            {
                var editor = ctx.Editor;
                editor.CaretOffset = Math.Clamp(
                    activeEdit.Edits.Min(edit => edit.Start),
                    0,
                    activeDocument.Document.TextLength);
                editor.TextArea.Caret.BringCaretToView();
                editor.Focus();
            }
        }

        /// <summary>Hook for MainWindowViewModel.WorkspaceEditRequested.</summary>
        public Task<bool> ApplyServerEditAsync(JsonElement workspaceEdit)
            => Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    var changes = LspHelpers.ParseWorkspaceEdit(workspaceEdit);
                    if (changes.Count == 0)
                        return false;
                    Apply(changes);
                    ctx.Status("Applied language-server edits. Review and save modified documents.");
                    return true;
                }
                catch (Exception ex)
                {
                    ctx.Status($"Couldn't apply language-server edits: {ex.Message}");
                    return false;
                }
            }).GetTask();
    }
}