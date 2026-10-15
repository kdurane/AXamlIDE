using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AXaxmlIDE.Lsp;
using AXaxmlIDE.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXaxmlIDE.ViewModels;

public partial class EditorPaneViewModel: ObservableObject, IDisposable
{
    [ObservableProperty] public partial OpenDocument? ActiveDocument { get; set; }
    [ObservableProperty] public partial string LspStatus { get; set; } = "language servers: idle";
    
    [RelayCommand]
    private async Task FormatActiveDocumentAsync()
    {
        if (ActiveDocument is not { } document)
        {
            ShowStatusMessage("Open a document with an active language server to format it.");
            return;
        }

        await FormatDocumentAsync(document, reportUnchanged: true);
    }
    
    private async Task<bool> FormatDocumentAsync(OpenDocument document, bool reportUnchanged)
    {
        if (document.Client is not { } client || document.Sync is not { } sync)
        {
            if (reportUnchanged)
                ShowStatusMessage("Open a document with an active language server to format it.");
            return false;
        }

        if (!client.SupportsDocumentFormatting)
        {
            if (reportUnchanged)
                ShowStatusMessage("The active language server does not support document formatting.");
            return false;
        }

        try
        {
            sync.Flush();
            var edits = await client.FormattingAsync(
                document.Uri, tabSize: 4, insertSpaces: true);
            if (edits.Count == 0)
            {
                if (reportUnchanged)
                    ShowStatusMessage("The language server returned no formatting changes.");
                return true;
            }

            ApplyTextEdits(document, edits.Select(edit => (edit.Range, edit.NewText)));
            if (reportUnchanged)
                ShowStatusMessage("Document formatted. Review and save the changes.");
            return true;
        }
        catch (Exception ex)
        {
            ShowStatusMessage($"Document formatting failed: {ex.Message}");
            return false;
        }
    }
    
    private static void ApplyTextEdits(
        OpenDocument document,
        IEnumerable<(LspRange Range, string NewText)> textEdits)
    {
        if (document.IsReadOnly)
            throw new InvalidOperationException($"Cannot edit read-only document '{document.Path}'.");

        var offsets = textEdits.Select(edit =>
        {
            var start = document.OffsetOf(edit.Range.StartLine, edit.Range.StartChar);
            var end = document.OffsetOf(edit.Range.EndLine, edit.Range.EndChar);
            if (end < start)
                throw new InvalidOperationException(
                    $"The language server returned an invalid edit range for '{document.Path}'.");
            return (Start: start, End: end, edit.NewText);
        }).OrderByDescending(edit => edit.Start).ToList();

        for (var index = 1; index < offsets.Count; index++)
            if (offsets[index - 1].Start < offsets[index].End)
                throw new InvalidOperationException(
                    $"The language server returned overlapping edits for '{document.Path}'.");

        foreach (var edit in offsets)
            document.Document.Replace(edit.Start, edit.End - edit.Start, edit.NewText);
    }
    
    public void ShowStatusMessage(string message) => LspStatus = message;
    
    [RelayCommand]
    private void CopyMouse(TextArea textArea) => ApplicationCommands.Copy.Execute(null, textArea);

    [RelayCommand]
    private void CutMouse(TextArea textArea) => ApplicationCommands.Cut.Execute(null, textArea);

    [RelayCommand]
    private void PasteMouse(TextArea textArea) => ApplicationCommands.Paste.Execute(null, textArea);

    [RelayCommand]
    private void SelectAllMouse(TextArea textArea) => ApplicationCommands.SelectAll.Execute(null, textArea);

    [RelayCommand]
    private void UndoMouse(TextArea textArea) => ApplicationCommands.Undo.Execute(null, textArea);

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}