using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// What goes with the next message besides the current file (VS Code's attachments): files from the + button or
/// dropped on the panel, and pictures pasted into the prompt box (AgentChatView.axaml.cs reads the clipboard and
/// the drop, and hands them here).
/// </summary>
public sealed partial class AgentChatViewModel
{
    /// <summary>Bigger pictures go as a file the agent can open, not inside the message.</summary>
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private int _pastedCount;

    /// <summary>The chips above the prompt box; sent with the next message, then cleared.</summary>
    public ObservableCollection<PromptAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>The agent takes pictures in a message (claude-agent-acp does).</summary>
    private bool CanSendImages => _client?.CanSendImages == true;

    /// <summary>The + button: pick files to send with the message.</summary>
    [RelayCommand]
    private async Task AddFiles()
        => AttachFiles(await _dialogs.PickFilesAsync("Add Files to the Message", WorkspaceFolder));

    /// <summary>
    /// Files (or folders) to send with the message. A picture is sent as a picture when the agent takes them, so
    /// Claude sees it without opening it; anything else goes as a link the agent reads itself.
    /// </summary>
    public void AttachFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Attachments.Any(attachment => attachment.FilePath == path))
                continue;
            if (CanSendImages && PromptAttachment.ImageMimeType(path) is { } mimeType
                && new FileInfo(path) is { Exists: true, Length: <= MaxImageBytes }
                && PromptAttachment.ForImage(File.ReadAllBytes(path), mimeType, Path.GetFileName(path), path) is { } image)
                Attachments.Add(image);
            else if (File.Exists(path) || Directory.Exists(path))
                Attachments.Add(PromptAttachment.ForFile(path));
        }
        OnPropertyChanged(nameof(HasAttachments));
    }

    /// <summary>A picture pasted into the prompt box (a screenshot), as PNG bytes.</summary>
    public void AttachPastedImage(byte[] png)
    {
        if (!CanSendImages)
        {
            _messenger.Send(new StatusMessage("Claude: this agent doesn't take pictures in messages."));
            return;
        }
        if (PromptAttachment.ForImage(png, "image/png", $"Pasted image {++_pastedCount}", filePath: null) is { } image)
        {
            Attachments.Add(image);
            OnPropertyChanged(nameof(HasAttachments));
        }
    }

    /// <summary>The × on an attachment chip.</summary>
    [RelayCommand]
    private void RemoveAttachment(PromptAttachment? attachment)
    {
        if (attachment is not null && Attachments.Remove(attachment))
            OnPropertyChanged(nameof(HasAttachments));
    }

    /// <summary>Everything the next message carries (the current file first), and clear the chips for the one after.</summary>
    private List<PromptAttachment> TakeAttachments()
    {
        var all = new List<PromptAttachment>();
        if (IncludeActiveFile && ActiveFilePath is { } file && Attachments.All(attachment => attachment.FilePath != file))
            all.Add(PromptAttachment.ForFile(file));
        all.AddRange(Attachments);
        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        return all;
    }
}
