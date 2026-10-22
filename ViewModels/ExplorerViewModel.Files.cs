using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Search;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The explorer's file operations (right-click menu, and F2 / Delete / Ctrl+X/C/V in the tree): add a new item or
/// folder, cut, copy, paste, rename, delete to the trash. The work on disk is <see cref="FileOperations"/>; this keeps open documents and
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

    /// <summary>
    /// Add > New Item... (Ctrl+Shift+A): Visual Studio's Add New Item into the node's folder (the selection's when
    /// there's no node, from a key or the File menu), then open what it made.
    /// </summary>
    [RelayCommand]
    private Task NewItem(ProjectTreeNode? node) => AddItemAsync(node, null);

    /// <summary>Add > Class... (Shift+Alt+C): Add New Item on the Class template, as in Visual Studio.</summary>
    [RelayCommand]
    private Task NewClass(ProjectTreeNode? node) => AddItemAsync(node, "Class");

    private async Task AddItemAsync(ProjectTreeNode? node, string? templateName)
    {
        if (TargetFolder(node) is not var (folderNode, folder))
        {
            Status("Open a folder or solution first.");
            return;
        }
        if (await dialogs.AddNewItemAsync(folder, Workspace.Roots.FirstOrDefault()?.FullPath is { } root ? FolderOf(root) : null, templateName)
            is not [var main, ..] files)
            return;

        try
        {
            ItemTemplates.Write(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't add '{Path.GetFileName(main.Path)}': {ex.Message}");
            return;
        }

        await ReloadAsync(folderNode, select: main.Path);
        documents.OpenAndActivate(main.Path);
        Status(files.Count == 1 ? $"Added {Path.GetFileName(main.Path)}." : $"Added {string.Join(", ", files.Select(file => Path.GetFileName(file.Path)))}.");
    }

    /// <summary>Add > New Folder... (Shift+Alt+N).</summary>
    [RelayCommand]
    private async Task NewFolder(ProjectTreeNode? node)
    {
        if (TargetFolder(node) is not var (folderNode, folder))
        {
            Status("Open a folder or solution first.");
            return;
        }
        if (await dialogs.PromptForTextAsync("New Folder", "Folder name", "Add", "NewFolder") is not { } name || name.Trim().Length == 0)
            return;
        name = name.Trim();
        if (FileOperations.InvalidNameReason(name) is { } reason)
        {
            Status(reason);
            return;
        }
        var path = Path.Combine(folder, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            Status($"'{name}' already exists here.");
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't add the folder '{name}': {ex.Message}");
            return;
        }
        await ReloadAsync(folderNode, select: path);
    }

    /// <summary>Where a new item goes: the node's folder; with no node, the selection's, else the workspace root's.</summary>
    private (ProjectTreeNode Node, string Folder)? TargetFolder(ProjectTreeNode? node)
    {
        node ??= Workspace.SelectedNode ?? Workspace.Roots.FirstOrDefault();
        return node is not null && FolderFor(node) is { } folder ? (node, folder) : null;
    }

    /// <summary>A root's folder: itself, or the folder its .sln / .csproj is in.</summary>
    private static string FolderOf(string path) => Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

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

        if (isCut)
        {
            if (await MoveWithNamespacesAsync(source, node, folder, alwaysAsk: false) is null)
                return;
            _clipboard = null;
            PasteCommand.NotifyCanExecuteChanged();
            return;
        }

        string pasted;
        try
        {
            pasted = await Task.Run(() => FileOperations.Paste(source, folder, move: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't paste: {ex.Message}");
            return;
        }
        await ReloadAsync(node, select: pasted);

    }

    /// <summary>Move a file or folder into a folder, keeping its open tabs open. Returns the new path, or null.</summary>
    private async Task<string?> MoveAsync(string source, ProjectTreeNode targetNode, string folder)
    {
        if (!ReleaseOpenDocuments(source, out var reopen))
            return null;

        string moved;
        try
        {
            moved = await Task.Run(() => FileOperations.Paste(source, folder, move: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Reopen(reopen, source, source);
            Status($"Couldn't move: {ex.Message}");
            return null;
        }

        Reopen(reopen, source, moved);
        await UpdateProjectPathsAsync(source, moved);
        await ReloadAsync(FindNode(source) ?? targetNode);
        await ReloadAsync(targetNode, select: moved);
        return moved;
    }

    /// <summary>Whether a dragged node can be dropped on target (a folder, or a file meaning its folder).</summary>
    public bool CanDrop(ProjectTreeNode dragged, ProjectTreeNode target)
        => CanChange(dragged)
           && FolderFor(target) is { } folder
           && !FileOperations.IsSameOrInside(folder, dragged.FullPath)                          // not into itself
           && Path.GetDirectoryName(dragged.FullPath) != Path.TrimEndingDirectorySeparator(folder); // not where it is

    /// <summary>A node dropped on another in the explorer: ask, then move it.</summary>
    public async Task DropAsync(ProjectTreeNode dragged, ProjectTreeNode target)
    {
        if (!CanDrop(dragged, target) || FolderFor(target) is not { } folder)
            return;
        var source = dragged.FullPath;
        if (source.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase) && File.Exists(source[..^3]))
            source = source[..^3];   // the code-behind goes with its .axaml, never alone
        await MoveWithNamespacesAsync(source, target, folder, alwaysAsk: true);
    }

    /// <summary>
    /// Move, with Rider's "update namespaces": work out what the move would change in the code (NamespaceMove), ask
    /// (always for a drag; for Paste only when there are namespaces to update), move, then update them if wanted.
    /// Returns the new path, or null if it wasn't moved.
    /// </summary>
    private async Task<string?> MoveWithNamespacesAsync(string source, ProjectTreeNode targetNode, string folder, bool alwaysAsk)
    {
        var root = Workspace.Roots.FirstOrDefault()?.FullPath is { } workspace ? FolderOf(workspace) : folder;
        var openTexts = openDocuments.Documents.ToDictionary(
            document => Path.GetFullPath(document.Path), document => document.Document.Text, StringComparer.Ordinal);
        IReadOnlyList<NamespaceFileUpdate> updates;
        try
        {
            updates = await Task.Run(() => NamespaceMove.Plan(source, folder, root, openTexts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            updates = [];   // still offer the move itself
        }

        var choice = MoveChoice.Move;
        if (alwaysAsk || updates.Count > 0)
        {
            var codeBehind = FileOperations.CodeBehindOf(source) is null ? "" : " and its code-behind";
            var from = Path.GetFileName(Path.GetDirectoryName(source));
            var changes = updates   // each file, then what changes in it
                .SelectMany(update => update.Changes.Select(change => "    " + change).Prepend(Path.GetRelativePath(root, update.Path)))
                .ToList();
            choice = await dialogs.ConfirmMoveAsync(
                $"Move '{Path.GetFileName(source)}'{codeBehind} from '{from}' to '{Path.GetFileName(folder)}'?", changes);
            if (choice == MoveChoice.Cancel)
                return null;
        }

        var moved = await MoveAsync(source, targetNode, folder);
        if (moved is not null && choice == MoveChoice.MoveAndUpdateNamespaces)
            ApplyNamespaceUpdates(updates);
        return moved;
    }

    /// <summary>
    /// Write the namespace updates. The files on disk have already moved, so the project only builds again once the
    /// updates are saved too: closed files are written, and open ones are saved unless they already had unsaved
    /// edits of yours (those are changed and left for you to save). A file that changed since the plan was made is
    /// left alone.
    /// </summary>
    private void ApplyNamespaceUpdates(IReadOnlyList<NamespaceFileUpdate> updates)
    {
        var paths = updates.Select(update => update.Path).ToHashSet(StringComparer.Ordinal);
        var cleanBefore = openDocuments.Documents
            .Where(document => !document.IsDirty && !document.IsReadOnly && paths.Contains(Path.GetFullPath(document.Path)))
            .ToList();

        var plan = new WorkspaceReplacePlan(
            [.. updates.Select(update => new WorkspaceFileReplacement(update.Path, update.OriginalText, update.UpdatedText, update.Changes.Count))],
            updates.Sum(update => update.Changes.Count),
            LimitReached: false);
        var open = openDocuments.Documents.ToDictionary(
            document => Path.GetFullPath(document.Path), document => (ITextBuffer)document, StringComparer.Ordinal);
        var summary = new WorkspaceSearchService().ApplyReplacePlan(plan, open);
        foreach (var document in cleanBefore.Where(document => document.IsDirty))
        {
            try
            {
                openDocuments.Save(document);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status($"Couldn't save '{document.Title}': {ex.Message}");
            }
        }
        Status(summary.FailedFiles == 0
            ? $"Updated namespaces in {summary.ReplacedFiles} file(s)."
            : $"Updated namespaces in {summary.ReplacedFiles} file(s); {summary.FailedFiles} changed in the meantime and were left as they were.");
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
        await UpdateProjectPathsAsync(node.FullPath, renamed);
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

        if (!File.Exists(node.FullPath) && !Directory.Exists(node.FullPath))
            await UpdateProjectPathsAsync(node.FullPath, null);
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

    /// <summary>
    /// A file or folder moved (or was deleted: newPath null): fix the paths its project's .csproj names it by,
    /// e.g. &lt;AvaloniaResource Include="..."&gt; or &lt;ApplicationIcon&gt;.
    /// </summary>
    private async Task UpdateProjectPathsAsync(string oldPath, string? newPath)
    {
        if (model.FindProjectForDocument(oldPath) is not { } project)
            return;   // not in a project
        try
        {
            await Task.Run(() => model.Host.MoveItem(project, oldPath, newPath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Status($"Couldn't update {Path.GetFileName(project)}: {ex.Message}");
        }
    }

    private List<OpenDocument> DocumentsUnder(string path)
        => [.. openDocuments.Documents.Where(document => FileOperations.IsSameOrInside(document.Path, path))];

    /// <summary>
    /// Close the open documents at or under <paramref name="path"/> before it moves; false (and a message) when one
    /// has unsaved changes. <paramref name="reopen"/> gets the closed ones' paths and which one was in front.
    /// </summary>
    private bool ReleaseOpenDocuments(string path, out (List<string> Paths, string? Active) reopen)
    {
        var open = DocumentsUnder(path);
        if (FileOperations.CodeBehindOf(path) is { } codeBehind)
            open.AddRange(DocumentsUnder(codeBehind));   // it moves with the .axaml (Reopen maps it to the new place)
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
