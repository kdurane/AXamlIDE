using System;
using System.IO;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Explorer panel: the workspace tree plus what you can do to a node (open, refresh, build,
/// run, debug). The workspace itself (open/close, selection) is <see cref="WorkspaceViewModel"/>.
/// </summary>
public sealed partial class ExplorerViewModel(
    WorkspaceViewModel workspace,
    RunViewModel run,
    IDocumentHost documents,
    IMessenger messenger)
{
    public WorkspaceViewModel Workspace { get; } = workspace;

    /// <summary>Build / Run / Start Debugging for a node (the node is the command parameter).</summary>
    public RunViewModel Run { get; } = run;

    [RelayCommand]
    private void OpenFile(ProjectTreeNode? node)
    {
        if (node is not { Kind: NodeKind.File } || string.IsNullOrEmpty(node.FullPath))
            return;
        try
        {
            documents.OpenAndActivate(node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status($"Couldn't open '{node.FullPath}': {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(ProjectTreeNode? node)
    {
        if (node is null)
            return;
        try
        {
            await Workspace.RefreshAsync(node);
        }
        catch (Exception ex)
        {
            Status($"Couldn't refresh '{node.Name}': {ex.Message}");
        }
    }

    public void Status(string text) => messenger.Send(new StatusMessage(text));
}
