using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The explorer's file operations (right-click menu, and F2 / Delete / Ctrl+X/C/V in the tree): cut, copy, paste,
/// rename, delete to the trash. The work on disk is <see cref="FileOperations"/>; this keeps open documents and
/// the tree in step. A renamed or moved file that's open is closed and reopened at its new path (refused while it
/// has unsaved changes); a deleted one is closed.
/// </summary>
public sealed partial class ExplorerViewModel
{
    /// <summary>What Cut/Copy put aside for Paste (the explorer's own clipboard: paths, not file contents).</summary>
    private (string Path, bool IsCut)? _clipboard;

    /// <summary>Files and folders can be cut, copied, renamed and deleted; the workspace root, solutions and projects can't.</summary>
    public bool CanChange(ProjectTreeNode? node)
        => node is { Kind: NodeKind.File or NodeKind.Directory or NodeKind.Folder, FullPath.Length: > 0 }
           && !Workspace.Roots.Contains(node);

    public bool HasClipboard => _clipboard is not null;

    [RelayCommand]
    private void Cut(ProjectTreeNode? node) => PutAside(node, isCut: true);

    [RelayCommand]
    private void Copy(ProjectTreeNode? node) => PutAside(node, isCut: false);

    private void PutAside(ProjectTreeNode? node, bool isCut)
    {
        if (!CanChange(node))
            return;
        _clipboard = (node!.FullPath, isCut);
        PasteCommand.NotifyCanExecuteChanged();
        Status($"{(isCut ? "Cut" : "Copied")} '{node.Name}': paste it into a folder.");
    }

    /// <summary>Paste into the node if it's a folder, else into the folder the node is in.</summary>
    [RelayCommand(CanExecute = nameof(HasClipboard))]
    private async Task Paste(ProjectTreeNode? node)
    {
        if (_clipboard is not var (source, isCut) || node is null || FolderFor(node) is not { } folder)
            return;
        (List<string> Paths, string? Active) reopen = ([], null);
        if (isCut && !ReleaseOpenDocuments(source, out reopen))
            return;

        string pasted;
        try
        {
            pasted = await Task.Run(() => FileOperations.Paste(source, folder, move: isCut));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't paste: {ex.Message}");
            return;
        }

        if (isCut)
        {
            _clipboard = null;   // moved: there's nothing left to paste again
            PasteCommand.NotifyCanExecuteChanged();
            Reopen(reopen, source, pasted);
            await ReloadAsync(FindNode(source) ?? node);   // where it came from
        }
        await ReloadAsync(node, select: pasted);
    }

    [RelayCommand]
    private async Task Rename(ProjectTreeNode? node)
    {
        if (!CanChange(node))
            return;
        if (await dialogs.PromptForTextAsync($"Rename '{node!.Name}'", "New name", "Rename", node.Name) is not { } name
            || name.Trim() == node.Name)
            return;
        if (FileOperations.InvalidNameReason(name.Trim()) is { } reason)
        {
            Status(reason);
            return;
        }
        if (!ReleaseOpenDocuments(node.FullPath, out var reopen))
            return;

        string renamed;
        try
        {
            renamed = FileOperations.Rename(node.FullPath, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Reopen(reopen, node.FullPath, node.FullPath);
            Status($"Couldn't rename '{node.Name}': {ex.Message}");
            return;
        }

        Reopen(reopen, node.FullPath, renamed);
        await ReloadAsync(node, select: renamed);
    }

    /// <summary>Move to the trash after asking; if there's no trash here, offer to delete permanently.</summary>
    [RelayCommand]
    private async Task Delete(ProjectTreeNode? node)
    {
        if (!CanChange(node))
            return;
        var open = DocumentsUnder(node!.FullPath);
        var unsaved = open.Any(document => document.IsDirty);
        var what = Directory.Exists(node.FullPath) ? $"the folder '{node.Name}' and everything in it" : $"'{node.Name}'";
        if (!await dialogs.ConfirmAsync("Delete",
                $"Move {what} to the trash?" + (unsaved ? " It has unsaved changes, which will be lost." : ""), "Delete"))
            return;

        foreach (var document in open)
            openDocuments.Close(document, force: true);

        try
        {
            if (!await Task.Run(() => FileOperations.TryMoveToTrash(node.FullPath)))
            {
                if (!await dialogs.ConfirmAsync("Delete Permanently",
                        $"There's no trash available here. Delete {what} permanently? This can't be undone.", "Delete Permanently"))
                    return;
                await Task.Run(() => FileOperations.DeletePermanently(node.FullPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't delete '{node.Name}': {ex.Message}");
        }

        await ReloadAsync(node);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>The folder a paste goes into: the node itself if it's a folder (or a project's folder), else its folder.</summary>
    private static string? FolderFor(ProjectTreeNode node) => node.Kind switch
    {
        NodeKind.Directory or NodeKind.Folder => node.FullPath.Length > 0 ? node.FullPath : null,
        NodeKind.Project or NodeKind.Solution or NodeKind.File => Path.GetDirectoryName(node.FullPath),
        _ => null
    };

    private List<OpenDocument> DocumentsUnder(string path)
        => openDocuments.Documents.Where(document => FileOperations.IsSameOrInside(document.Path, path)).ToList();

    /// <summary>
    /// Close the open documents at or under <paramref name="path"/> before it moves; false (and a message) when one
    /// has unsaved changes. <paramref name="reopen"/> gets the closed ones' paths and which one was in front.
    /// </summary>
    private bool ReleaseOpenDocuments(string path, out (List<string> Paths, string? Active) reopen)
    {
        var open = DocumentsUnder(path);
        reopen = (open.Select(document => document.Path).ToList(), open.Contains(openDocuments.Active!) ? openDocuments.Active!.Path : null);
        if (open.FirstOrDefault(document => document.IsDirty) is { } unsaved)
        {
            Status($"Save or close '{unsaved.Title}' first: it has unsaved changes.");
            return false;
        }
        foreach (var document in open)
            openDocuments.Close(document, force: true);
        return true;
    }

    /// <summary>Open the documents closed by <see cref="ReleaseOpenDocuments"/> again, at their new paths.</summary>
    private void Reopen((List<string> Paths, string? Active) closed, string oldPath, string newPath)
    {
        foreach (var path in closed.Paths)
        {
            var moved = newPath + path[oldPath.Length..];
            try
            {
                var document = openDocuments.Open(moved);
                if (path == closed.Active)
                    openDocuments.Active = document;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status($"Couldn't reopen '{Path.GetFileName(moved)}': {ex.Message}");
            }
        }
    }

    private ProjectTreeNode? FindNode(string path)
        => Workspace.Roots.Select(root => WorkspaceViewModel.FindByPath(root, path)).FirstOrDefault(node => node is not null);

    /// <summary>Reload the folder or project around <paramref name="node"/>, then select <paramref name="select"/> if it's there.</summary>
    private async Task ReloadAsync(ProjectTreeNode node, string? select = null)
    {
        var reloaded = await Workspace.ReloadAroundAsync(node);
        if (select is not null && reloaded is not null && WorkspaceViewModel.FindByPath(reloaded, select) is { } selected)
            Workspace.SelectedNode = selected;
    }
}
