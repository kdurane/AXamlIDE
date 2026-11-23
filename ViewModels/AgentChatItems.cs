using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Agents;
using AXamlIDE.Services.Git;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

// The rows of the Claude panel's conversation (AgentChatViewModel.Items). Views/AgentChatView.axaml has a
// DataTemplate for each.

public abstract class AgentChatItem : ObservableObject;

/// <summary>What the user sent, and the files and pictures that went with it.</summary>
public sealed partial class UserMessageItem(string text, IReadOnlyList<PromptAttachment> attachments) : AgentChatItem
{
    /// <summary>Grows while a reopened chat is replayed (the agent sends old messages in pieces).</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    public IReadOnlyList<PromptAttachment> Attachments { get; } = attachments;
    public bool HasAttachments => Attachments.Count > 0;
}

/// <summary>
/// A file or picture sent with a message: a chip above the prompt box until it's sent, then under the message.
/// A picture goes inside the message (<see cref="AcpPromptBlock.Image"/>); a file as a link the agent opens.
/// </summary>
public sealed class PromptAttachment
{
    private PromptAttachment(string name, string? filePath, byte[]? imageData, string? mimeType, Bitmap? thumbnail)
    {
        Name = name;
        FilePath = filePath;
        ImageData = imageData;
        MimeType = mimeType;
        Thumbnail = thumbnail;
    }

    public string Name { get; }

    /// <summary>Where it came from; null for a pasted picture.</summary>
    public string? FilePath { get; }

    public byte[]? ImageData { get; }
    public string? MimeType { get; }

    /// <summary>A small copy of the picture for the chip; null for a file.</summary>
    public Bitmap? Thumbnail { get; }

    public bool IsImage => Thumbnail is not null;
    public string ToolTip => FilePath ?? Name;

    public static PromptAttachment ForFile(string path)
        => new(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, null, null, null);

    /// <summary>A picture, or null if its bytes aren't a picture Avalonia can read.</summary>
    public static PromptAttachment? ForImage(byte[] data, string mimeType, string name, string? filePath)
    {
        try
        {
            using var stream = new MemoryStream(data);
            return new PromptAttachment(name, filePath, data, mimeType, Bitmap.DecodeToHeight(stream, 64));
        }
        catch (Exception)
        {
            return null;   // not a picture after all (a damaged file, an unknown format)
        }
    }

    /// <summary>The picture types Claude reads, by file extension; null for anything else.</summary>
    public static string? ImageMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null
    };

    public AcpPromptBlock ToPromptBlock()
        => ImageData is { } data ? new AcpPromptBlock.Image(data, MimeType!) : new AcpPromptBlock.File(FilePath!);
}

/// <summary>The agent's reply (Markdown) or its thinking, growing as the pieces stream in.</summary>
public sealed partial class AgentTextItem(bool isThought, string? messageId) : AgentChatItem
{
    public bool IsThought { get; } = isThought;
    public string? MessageId { get; } = messageId;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Thinking is shown folded, like VS Code's chat.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>
/// Something the panel says itself: an error, the turn being stopped, a sign-in problem.
/// </summary>
public sealed class NoticeItem(string text, bool isError) : AgentChatItem
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>One line of an edit's diff, ready to colour.</summary>
public sealed record AgentDiffLine(string Text, DiffLineKind Kind)
{
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public string Marker => Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => " " };
}

/// <summary>An edit a tool call makes to one file. A long one shows its first <see cref="MaxLines"/> lines.</summary>
public sealed class AgentDiffItem
{
    private const int MaxLines = 200;

    public AgentDiffItem(AcpDiffContent diff)
    {
        Path = diff.Path;
        IsNewFile = diff.OldText is null;
        var lines = InlineDiff.Compare(diff.OldText, diff.NewText);
        Lines = [.. lines.Take(MaxLines).Select(line => new AgentDiffLine(line.Text, line.Kind))];
        MoreLines = lines.Count > MaxLines ? $"... {lines.Count - MaxLines} more lines (open the file to see them)" : null;
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsNewFile { get; }
    public IReadOnlyList<AgentDiffLine> Lines { get; }
    public string? MoreLines { get; }
    public bool HasMoreLines => MoreLines is not null;
}

/// <summary>
/// A tool the agent runs (read a file, edit, run a command, search...). Its updates arrive piece by piece:
/// <see cref="Apply"/> merges each one. Click the header to show what it did; edits start open.
/// </summary>
public sealed partial class ToolCallItem : AgentChatItem
{
    private readonly AgentChatViewModel _owner;

    public ToolCallItem(AgentChatViewModel owner, AcpToolCall call)
    {
        _owner = owner;
        Id = call.Id;
        Apply(call);
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(ShownCommand), nameof(HasCommand), nameof(CommandMarkdown))]
    public partial string Title { get; set; } = "Tool";

    /// <summary>read, edit, delete, move, search, execute, think, fetch, switch_mode or other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey), nameof(Heading), nameof(ShownCommand), nameof(HasCommand), nameof(CommandMarkdown))]
    public partial string Kind { get; set; } = "other";

    /// <summary>A terminal command's text, which the agent also sends as the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(ShownCommand), nameof(HasCommand), nameof(CommandMarkdown))]
    public partial string? Command { get; set; }

    /// <summary>The agent's one-line summary of a command ("List files in current directory").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial string? Description { get; set; }

    private bool IsCommand => Kind == "execute";

    /// <summary>
    /// The command, shown only when the row is expanded, as VS Code does: a script can be many lines of shorthand.
    /// The agent puts the command in the title too, so that's used when the input didn't come with it.
    /// </summary>
    public string? ShownCommand => IsCommand ? Command is { Length: > 0 } command ? command : Title : null;

    public bool HasCommand => ShownCommand is { Length: > 0 };

    /// <summary>The command as a shell code block, for MarkdownText to colour.</summary>
    public string CommandMarkdown => CommandBlock(ShownCommand);

    /// <summary>A fenced bash block; the fence is longer than any run of backticks in the command, so it can't end early.</summary>
    public static string CommandBlock(string? command)
    {
        if (command is not { Length: > 0 })
            return "";
        int longest = 0, run = 0;
        foreach (var c in command)
            longest = Math.Max(longest, run = c == '`' ? run + 1 : 0);
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}bash\n{command.TrimEnd()}\n{fence}";
    }

    /// <summary>The row's one line: for a command, its summary (or its first line); otherwise the title.</summary>
    public string Heading => IsCommand ? CommandHeading(Description, ShownCommand) ?? Title : Title;

    /// <summary>The summary, or else the command's first line, with "…" when there's more to it.</summary>
    public static string? CommandHeading(string? description, string? command)
    {
        if (description?.Trim() is { Length: > 0 } summary)
            return summary;
        if (command?.Trim() is not { Length: > 0 } text)
            return null;
        var end = text.IndexOf('\n');
        return end < 0 ? text : text[..end].TrimEnd() + " …";
    }

    /// <summary>pending, in_progress, completed or failed; "cancelled" when the turn was stopped first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailed), nameof(IsDone))]
    public partial string Status { get; set; } = "pending";

    public bool IsRunning => Status is "pending" or "in_progress";
    public bool IsFailed => Status is "failed" or "cancelled";
    public bool IsDone => Status == "completed";

    /// <summary>The Icon.* resource for the kind of tool (Views/AgentChatView picks it with IconKeyConverter).</summary>
    public string IconKey => Kind switch
    {
        "read" => "Icon.FileDocumentOutline",
        "edit" => "Icon.FileEdit",
        "delete" => "Icon.TrashCan",
        "search" => "Icon.Search",
        "execute" => "Icon.Console",
        "fetch" => "Icon.Download",
        _ => "Icon.Cog"
    };

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>The tool's text output (Markdown, often a code block).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(HasDetails))]
    public partial string Output { get; set; } = "";

    public bool HasOutput => Output.Length > 0;

    public ObservableCollection<AgentDiffItem> Diffs { get; } = [];

    /// <summary>The files it works on; click one to open it at the line.</summary>
    public ObservableCollection<AcpLocation> Locations { get; } = [];

    public bool HasDetails => HasOutput || Diffs.Count > 0;

    /// <summary>The file links under the title; an edit's diff already names its file, so they're left out then.</summary>
    public bool ShowLocations => Locations.Count > 0 && Diffs.Count == 0;

    public void Apply(AcpToolCall call)
    {
        if (call.Title is { Length: > 0 } title)
            Title = title;
        if (call.Kind is { Length: > 0 } kind)
            Kind = kind;
        if (call.Status is { Length: > 0 } status)
            Status = status;
        if (call.Command is { Length: > 0 } command)
            Command = command;
        if (call.Description is { Length: > 0 } description)
            Description = description;

        if (call.Content is { } content)
        {
            // A command's first content is its summary, already the heading, so it isn't shown twice.
            Output = string.Join("\n\n", content.OfType<AcpTextContent>().Select(text => text.Text)
                .Where(text => !(IsCommand && text.Trim() == Description?.Trim())));
            var diffs = content.OfType<AcpDiffContent>().ToList();
            if (diffs.Count > 0 || Diffs.Count > 0)
            {
                Diffs.Clear();
                foreach (var diff in diffs)
                    Diffs.Add(new AgentDiffItem(diff));
                IsExpanded |= Diffs.Count > 0;   // an edit is worth seeing straight away
            }
            OnPropertyChanged(nameof(HasDetails));
        }

        if (call.Locations is { } locations)
        {
            Locations.Clear();
            foreach (var location in locations.DistinctBy(location => location.Path))
                Locations.Add(location);
        }
        OnPropertyChanged(nameof(ShowLocations));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void OpenLocation(AcpLocation? location)
    {
        if (location is not null)
            _owner.OpenFile(location.Path, location.Line);
    }

    [RelayCommand]
    private void OpenDiffFile(AgentDiffItem? diff)
    {
        if (diff is not null)
            _owner.OpenFile(diff.Path, null);
    }
}

/// <summary>
/// The agent asks before running a tool: one button per choice (Yes / Always / No). The answer goes back to the
/// agent and the buttons are replaced by what was chosen. Stopping the turn answers "cancelled".
/// </summary>
public sealed partial class PermissionItem : AgentChatItem
{
    private readonly TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PermissionItem(AcpPermissionRequest request)
    {
        var call = request.ToolCall;
        Title = call.Title ?? "Run a tool";
        Detail = call.Content?.OfType<AcpTextContent>().FirstOrDefault()?.Text ?? call.Description;
        Options = request.Options;

        // A terminal command: its summary up top, the script itself behind "Show command".
        if (call.Command is { Length: > 0 } command)
        {
            Command = command;
            Title = ToolCallItem.CommandHeading(Detail, command) ?? Title;
            if (Detail?.Trim() == Title)
                Detail = null;
        }
    }

    public string Title { get; }

    /// <summary>The command it asks to run, shown when expanded.</summary>
    public string? Command { get; }

    public bool HasCommand => Command is { Length: > 0 };

    public string CommandMarkdown => ToolCallItem.CommandBlock(Command);

    [ObservableProperty]
    public partial bool IsCommandShown { get; set; }

    /// <summary>What the tool says it's for ("Create empty file made.txt").</summary>
    public string? Detail { get; }

    public bool HasDetail => Detail is { Length: > 0 };

    public IReadOnlyList<AcpPermissionOption> Options { get; }

    public Task<string?> Answer => _answer.Task;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial string? Outcome { get; set; }

    public bool IsWaiting => Outcome is null;

    [RelayCommand]
    private void Choose(AcpPermissionOption? option)
    {
        if (option is null || !IsWaiting)
            return;
        Outcome = option.Allows ? $"Allowed: {option.Name}" : $"Denied: {option.Name}";
        _answer.TrySetResult(option.Id);
    }

    /// <summary>The turn stopped before the user answered.</summary>
    public void Cancel()
    {
        if (!IsWaiting)
            return;
        Outcome = "Cancelled";
        _answer.TrySetResult(null);
    }
}

/// <summary>The agent's to-do list for the task, updated in place as it works through it.</summary>
public sealed partial class PlanItem : AgentChatItem
{
    [ObservableProperty]
    public partial IReadOnlyList<AgentPlanRow> Entries { get; set; } = [];

    public void Show(IReadOnlyList<AcpPlanEntry> entries) => Entries = [.. entries.Select(entry => new AgentPlanRow(entry.Content, entry.Status))];
}

/// <summary>One step of the plan: done (ticked), being done (highlighted), or still to do.</summary>
public sealed record AgentPlanRow(string Content, string Status)
{
    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public bool IsPending => !IsCompleted && !IsInProgress;
}

/// <summary>
/// One of the session's settings as a drop-down under the prompt box (Mode, Model). Picking a choice sends it to the
/// agent; the agent's answer (or a config_option_update) sets what's shown.
/// </summary>
public sealed partial class AgentSettingItem : ObservableObject
{
    private readonly AgentChatViewModel _owner;
    private bool _updating;

    public AgentSettingItem(AgentChatViewModel owner, AcpConfigOption option)
    {
        _owner = owner;
        Id = option.Id;
        Name = option.Name;
        Update(option);
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>
    /// Shown before the drop-down ("Effort:"), so "Default" says what it's the default of.
    /// Mode and Model need none: their choices say what they are, as in VS Code's chat.
    /// </summary>
    public string Label => Id is "mode" or "model" ? "" : Name + ":";

    public ObservableCollection<AcpChoice> Choices { get; } = [];

    [ObservableProperty]
    public partial AcpChoice? Selected { get; set; }

    /// <summary>Show what the agent says the setting now is (without sending it back).</summary>
    public void Update(AcpConfigOption option)
    {
        _updating = true;
        try
        {
            if (!Choices.Select(choice => choice.Value).SequenceEqual(option.Choices.Select(choice => choice.Value)))
            {
                Choices.Clear();
                foreach (var choice in option.Choices)
                    Choices.Add(choice);
            }
            Selected = Choices.FirstOrDefault(choice => choice.Value == option.CurrentValue);
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnSelectedChanged(AcpChoice? value)
    {
        if (!_updating && value is not null)
            _ = _owner.SetSettingAsync(Id, value.Value);
    }
}
