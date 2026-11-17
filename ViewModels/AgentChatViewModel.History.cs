using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Chat history (the header's clock button, like VS Code's "Show Chats..."): the past conversations in the workspace
/// folder, from the agent (session/list), and reopening one (session/load), which replays it into the conversation.
/// Claude Code keeps sessions per folder in ~/.claude, so chats from VS Code or the terminal are listed too.
/// </summary>
public sealed partial class AgentChatViewModel
{
    /// <summary>The history menu's entries: the chats, or one disabled line saying why there are none.</summary>
    public ObservableCollection<ChatHistoryItem> History { get; } = [];

    /// <summary>A past session is being replayed: no sending until it's back.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendToolTip))]
    public partial bool IsLoadingSession { get; set; }

    /// <summary>The agent is replaying a session: its user chunks are the old messages, so they're shown.</summary>
    private bool _replaying;

    /// <summary>The history button (also as the menu opens): ask the agent for the folder's chats.</summary>
    [RelayCommand]
    private async Task LoadHistory()
    {
        History.Clear();
        if (_client is not { } client || WorkspaceFolder is not { } folder)
        {
            History.Add(ChatHistoryItem.Note(_client is null ? "Claude isn't running." : "Open a folder to see its chats."));
            return;
        }
        if (!client.CanListSessions || !client.CanLoadSessions)
        {
            History.Add(ChatHistoryItem.Note("This version of the agent can't list past chats."));
            return;
        }

        History.Add(ChatHistoryItem.Note("Loading..."));
        try
        {
            var sessions = await client.ListSessionsAsync(folder);
            History.Clear();
            foreach (var session in sessions.OrderByDescending(session => session.UpdatedAt ?? DateTimeOffset.MinValue))
                History.Add(new ChatHistoryItem(this, session, isCurrent: session.Id == _session?.Id));
            if (History.Count == 0)
                History.Add(ChatHistoryItem.Note("No earlier chats in this folder."));
        }
        catch (AcpException ex)
        {
            History.Clear();
            History.Add(ChatHistoryItem.Note("Couldn't list the chats: " + ex.Message));
        }
    }

    /// <summary>Reopen a past chat: replaces the conversation shown, and the next message carries on from it.</summary>
    public async Task OpenSessionAsync(AcpSessionInfo session)
    {
        if (_client is not { } client || WorkspaceFolder is not { } folder || session.Id == _session?.Id)
            return;

        ClearConversation();   // not NewChat: that would start a fresh session alongside this one
        Title = string.IsNullOrWhiteSpace(session.Title) ? "Claude" : session.Title;
        // Set before asking, so the replayed updates (which arrive before the answer) are taken as this session's.
        _session = new AcpSession(session.Id, []);
        _sessionFolder = folder;
        _replaying = true;
        IsLoadingSession = true;
        try
        {
            var loaded = await client.LoadSessionAsync(session.Id, folder);
            // The replayed updates were posted to this thread before the answer came, but this continuation can run
            // ahead of them: let them through while _replaying still says they're old messages.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            if (_session?.Id != session.Id)
                return;   // New Chat (or another chat) while it loaded
            _session = loaded;
            ShowSettings(loaded.Options);
        }
        catch (AcpException ex)
        {
            if (_session?.Id == session.Id)
            {
                _session = null;
                _sessionFolder = null;
            }
            if (ReferenceEquals(client, _client))
                Items.Add(new NoticeItem("Couldn't open that chat: " + ex.Message, isError: true));
        }
        finally
        {
            _replaying = false;
            IsLoadingSession = false;
            // A replayed tool call that never said it finished (the chat was stopped mid-tool) isn't running now.
            foreach (var call in _toolCalls.Values.Where(call => call.IsRunning))
                call.Status = "cancelled";
            _toolCalls.Clear();
        }
    }
}

/// <summary>One entry in the history menu: a past chat (its title and when it was last used), or a note.</summary>
public sealed partial class ChatHistoryItem : ObservableObject
{
    private readonly AgentChatViewModel? _owner;
    private readonly AcpSessionInfo? _session;

    public ChatHistoryItem(AgentChatViewModel owner, AcpSessionInfo session, bool isCurrent)
    {
        _owner = owner;
        _session = session;
        var title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled chat" : session.Title.Trim();
        if (title.Length > 60)
            title = title[..57] + "...";
        var when = session.UpdatedAt is { } updated ? "  ·  " + Ago(updated) : "";
        Label = (isCurrent ? "✓ " : "") + title + when;
        IsEnabled = !isCurrent;
    }

    private ChatHistoryItem(string note)
    {
        Label = note;
        IsEnabled = false;
    }

    public static ChatHistoryItem Note(string text) => new(text);

    /// <summary>"Fix the build  ·  2 hours ago"; a tick on the chat that's open.</summary>
    public string Label { get; }

    /// <summary>False for a note and for the chat already open.</summary>
    public bool IsEnabled { get; }

    [RelayCommand]
    private Task Open() => _owner is { } owner && _session is { } session ? owner.OpenSessionAsync(session) : Task.CompletedTask;

    internal static string Ago(DateTimeOffset when, DateTimeOffset? now = null)
    {
        var age = (now ?? DateTimeOffset.Now) - when;
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => Plural((int)age.TotalMinutes, "minute"),
            < 60 * 24 => Plural((int)age.TotalHours, "hour"),
            < 60 * 24 * 7 => Plural((int)age.TotalDays, "day"),
            _ => when.ToLocalTime().ToString("d MMM yyyy")
        };
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";
}
