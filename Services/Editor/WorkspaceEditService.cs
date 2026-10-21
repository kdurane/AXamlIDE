using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// Applies LSP workspace edits (rename, code actions, server-initiated edits) to the documents
/// themselves, so it works the same whichever editor (or none) is showing them.
/// </summary>
public sealed class WorkspaceEditService
{
    private readonly IDocumentHost _documents;
    private readonly IMessenger _messenger;

    public WorkspaceEditService(IDocumentHost documents, LanguageSessionService languages, IMessenger messenger)
    {
        _documents = documents;
        _messenger = messenger;

        // A delegate rather than a message because the server needs the bool result back.
        languages.WorkspaceEditRequested = ApplyServerEditAsync;
    }

    public void Apply(Dictionary<string, List<(LspRange Range, string NewText)>> changes)
    {
        var prepared = new List<(OpenDocument Document, List<(int Start, int End, string Text)> Edits)>();
        foreach (var (path, textEdits) in changes)
        {
            var document = _documents.Open(path);
            if (document.IsReadOnly)
                throw new InvalidOperationException($"Cannot apply an edit to read-only document '{path}'.");
            var offsets = textEdits.Select(edit =>
            {
                var start = document.OffsetOf(edit.Range.StartLine, edit.Range.StartChar);
                var end = document.OffsetOf(edit.Range.EndLine, edit.Range.EndChar);
                return end < start
                    ? throw new InvalidOperationException($"Rename returned an invalid range for '{path}'.")
                    : (Start: start, End: end, Text: edit.NewText);
            }).OrderByDescending(edit => edit.Start).ToList();

            for (var index = 1; index < offsets.Count; index++)
                if (offsets[index - 1].Start < offsets[index].End)
                    throw new InvalidOperationException($"Rename returned overlapping edits for '{path}'.");

            prepared.Add((document, offsets));
        }

        foreach (var (document, edits) in prepared)
            foreach (var (Start, End, Text) in edits)
                document.Document.Replace(Start, End - Start, Text);

        // Move the caret of the selected tab to its first edit.
        if (prepared.FirstOrDefault(item => ReferenceEquals(item.Document, _documents.Active)) is
            { Document: { } activeDocument, Edits.Count: > 0 } activeEdit)
            activeDocument.Reveal(activeEdit.Edits.Min(edit => edit.Start));
    }

    /// <summary>
    /// Applies a whole LSP workspace edit: creates the files it creates (left empty on disk until saved, like the
    /// other edited documents), then its text edits. False when there was nothing to do.
    /// </summary>
    public bool Apply(JsonElement workspaceEdit)
    {
        var created = false;
        foreach (var path in LspHelpers.FilesToCreate(workspaceEdit))
        {
            if (System.IO.File.Exists(path))
                continue;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "");
            created = true;
        }

        var changes = LspHelpers.ParseWorkspaceEdit(workspaceEdit);
        if (changes.Count > 0)
            Apply(changes);
        return created || changes.Count > 0;
    }

    /// <summary>Applies an edit the language server pushed to us (workspace/applyEdit).</summary>
    public Task<bool> ApplyServerEditAsync(JsonElement workspaceEdit)
        => Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                if (!Apply(workspaceEdit))
                    return false;
                Status("Applied language-server edits. Review and save modified documents.");
                return true;
            }
            catch (Exception ex)
            {
                Status($"Couldn't apply language-server edits: {ex.Message}");
                return false;
            }
        }).GetTask();

    private void Status(string text) => _messenger.Send(new StatusMessage(text));
}
