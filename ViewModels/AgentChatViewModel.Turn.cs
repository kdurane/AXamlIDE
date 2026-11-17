using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Agents;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// A prompt turn: the user's message goes to the agent, which streams back text, tool calls and its plan until it
/// answers the prompt. Each update is merged into <see cref="AgentChatViewModel.Items"/> on the UI thread.
/// </summary>
public sealed partial class AgentChatViewModel
{
    private readonly Dictionary<string, ToolCallItem> _toolCalls = [];
    private readonly List<PermissionItem> _pendingPermissions = [];

    /// <summary>The send button and Enter in the prompt box.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        if (_client is not { } client || WorkspaceFolder is not { } folder)
            return;

        var text = PromptText.Trim();
        var attachments = TakeAttachments();
        PromptText = "";
        Items.Add(new UserMessageItem(text, attachments));
        IsTurnRunning = true;
        try
        {
            if (await EnsureSessionAsync() is not { } session)
                return;

            List<AcpPromptBlock> prompt = [new AcpPromptBlock.Text(text), .. attachments.Select(attachment => attachment.ToPromptBlock())];

            var stopReason = await client.PromptAsync(session.Id, prompt);
            if (StopReasonNotice(stopReason) is { } notice)
                Items.Add(new NoticeItem(notice, isError: stopReason != "cancelled"));
        }
        catch (AcpException ex) when (ex.IsAuthRequired && ReferenceEquals(client, _client))
        {
            // The login expired: back in the box to send again once signed in (NeedSignIn reopens this chat).
            if (PromptText.Length == 0)
                PromptText = text;
            NeedSignIn(ExpiredReason);
        }
        catch (AcpException ex)
        {
            if (ReferenceEquals(client, _client))   // not the agent being stopped on purpose
                Items.Add(new NoticeItem(ex.Message, isError: true));
        }
        finally
        {
            if (ReferenceEquals(client, _client))
                IsTurnRunning = false;
            CancelPendingPermissions();
            foreach (var call in _toolCalls.Values.Where(call => call.IsRunning))
                call.Status = "cancelled";   // the agent finished without saying (stopped mid-tool)
            _toolCalls.Clear();
        }
    }

    private bool CanSend() => State == AgentChatState.Ready && !IsTurnRunning && !IsLoadingSession && !string.IsNullOrWhiteSpace(PromptText);

    /// <summary>
    /// The send button's tooltip. It's greyed out until there's something to send (as in VS Code), so it says why,
    /// rather than seeming not to work when clicked.
    /// </summary>
    public string SendToolTip => State switch
    {
        AgentChatState.Ready when IsLoadingSession => "Loading the chat...",
        AgentChatState.Ready when string.IsNullOrWhiteSpace(PromptText) => "Type a message to send",
        AgentChatState.Ready => "Send (Enter)",
        AgentChatState.Starting or AgentChatState.Installing or AgentChatState.Idle => "Claude is starting...",
        AgentChatState.SignIn => "Sign in to Claude first",
        AgentChatState.NotInstalled => "Download Claude first",
        _ => "Claude isn't running (Restart above)"
    };

    private Task<AcpSession?>? _sessionStarting;

    /// <summary>
    /// The conversation's session, started in the workspace folder if there isn't one yet. Started as soon as the agent
    /// is ready (and after New Chat) rather than on the first message, so the Mode and Model drop-downs show before
    /// anything is typed. Null when there's no agent or folder; a failure is shown in the conversation.
    /// </summary>
    private Task<AcpSession?> EnsureSessionAsync()
    {
        if (_session is { } session && _sessionFolder == WorkspaceFolder)
            return Task.FromResult<AcpSession?>(session);
        if (_sessionStarting is { } starting)
            return starting;

        // Shared while it runs, so two callers don't start two sessions. One that finished without waiting (no agent
        // or folder, or it failed at once) has already run its finally, which clears _sessionStarting: keeping it
        // would hand its null to every later call, and no message would ever be sent.
        var start = StartSessionAsync();
        if (!start.IsCompleted)
            _sessionStarting = start;
        return start;
    }

    private async Task<AcpSession?> StartSessionAsync()
    {
        try
        {
            if (_client is not { } client || WorkspaceFolder is not { } folder)
                return null;
            try
            {
                var session = await client.NewSessionAsync(folder);
                if (!ReferenceEquals(client, _client) || WorkspaceFolder != folder)
                {
                    client.CloseSession(session.Id);   // stopped, or another folder opened, while it started
                    return null;
                }
                if (_session is not null)
                {
                    client.CloseSession(session.Id);   // a reopened chat took over while it started
                    return _session;
                }
                _session = session;
                _sessionFolder = folder;
                ShowSettings(session.Options);
                return session;
            }
            catch (AcpException ex) when (ex.IsAuthRequired && ReferenceEquals(client, _client))
            {
                NeedSignIn(ExpiredReason);
                return null;
            }
            catch (AcpException ex)
            {
                if (ReferenceEquals(client, _client))
                    Items.Add(new NoticeItem("Couldn't start a conversation: " + ex.Message, isError: true));
                return null;
            }
        }
        finally
        {
            _sessionStarting = null;
        }
    }

    private static string? StopReasonNotice(string stopReason) => stopReason switch
    {
        "cancelled" => "Stopped.",
        "max_tokens" => "Claude stopped: the reply reached its length limit.",
        "max_turn_requests" => "Claude stopped: it reached the limit of steps for one message.",
        "refusal" => "Claude declined to continue.",
        _ => null
    };

    /// <summary>The stop button: ask the agent to stop, and answer its open questions with "cancelled".</summary>
    [RelayCommand(CanExecute = nameof(IsTurnRunning))]
    private void Stop()
    {
        if (_client is { } client && _session is { } session)
            client.Cancel(session.Id);
        CancelPendingPermissions();
    }

    /// <summary>Start a new conversation in a new session (the old one stays in the history).</summary>
    [RelayCommand]
    private void NewChat()
    {
        ClearConversation();
        if (State == AgentChatState.Ready)
            _ = EnsureSessionAsync();   // the drop-downs for the new chat
    }

    /// <summary>Close the session (it stays in the history) and empty the conversation.</summary>
    private void ClearConversation()
    {
        if (IsTurnRunning)
            Stop();
        if (_session is { } session)
            _client?.CloseSession(session.Id);
        _session = null;
        _sessionFolder = null;
        _toolCalls.Clear();
        Items.Clear();
        Title = "Claude";
        UsageText = "";
    }

    // ------------------------------------------------------------- the agent's updates

    private void OnUpdate(string sessionId, AcpUpdate update)
    {
        if (_session?.Id != sessionId)
            return;   // from a conversation that's been replaced

        switch (update)
        {
            case AcpMessageChunk { Kind: AcpChunkKind.User } user:
                // Live, it's our own message (already shown); replaying a reopened chat, it's an old one.
                if (!_replaying)
                    break;
                if (Items.LastOrDefault() is UserMessageItem growing)
                    growing.Text += user.Text;
                else
                    Items.Add(new UserMessageItem(user.Text, []));
                break;

            case AcpMessageChunk chunk:
                var isThought = chunk.Kind == AcpChunkKind.Thought;
                if (Items.LastOrDefault() is AgentTextItem last && last.IsThought == isThought
                    && (chunk.MessageId is null || last.MessageId is null || last.MessageId == chunk.MessageId))
                    last.Text += chunk.Text;
                else
                    Items.Add(new AgentTextItem(isThought, chunk.MessageId) { Text = chunk.Text });
                break;

            case AcpToolCall call:
                if (_toolCalls.TryGetValue(call.Id, out var item))
                {
                    item.Apply(call);
                }
                else
                {
                    item = new ToolCallItem(this, call);
                    _toolCalls[call.Id] = item;
                    Items.Add(item);
                }
                break;

            case AcpPlan plan:
                if (Items.OfType<PlanItem>().LastOrDefault() is not { } shown)
                    Items.Add(shown = new PlanItem());
                shown.Show(plan.Entries);
                break;

            case AcpConfigOptionsChanged options:
                ShowSettings(options.Options);
                break;

            case AcpSessionTitle title:
                Title = title.Title;
                break;

            case AcpUsage usage:
                UsageText = $"{Tokens(usage.Used)} / {Tokens(usage.Size)} tokens";
                break;
        }
    }

    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0}k",
        _ => count.ToString()
    };

    private void ShowSettings(IReadOnlyList<AcpConfigOption> options)
    {
        foreach (var option in options)
        {
            if (Settings.FirstOrDefault(setting => setting.Id == option.Id) is { } shown)
                shown.Update(option);
            else
                Settings.Add(new AgentSettingItem(this, option));
        }
        foreach (var gone in Settings.Where(setting => options.All(option => option.Id != setting.Id)).ToList())
            Settings.Remove(gone);
    }

    /// <summary>A drop-down changed: tell the agent (it answers with every setting as it now is).</summary>
    public async Task SetSettingAsync(string id, string value)
    {
        if (_client is not { } client || _session is not { } session)
            return;
        try
        {
            ShowSettings(await client.SetConfigOptionAsync(session.Id, id, value));
        }
        catch (AcpException ex)
        {
            Items.Add(new NoticeItem($"Couldn't change the setting: {ex.Message}", isError: true));
        }
    }

    // ------------------------------------------------------------- permission

    /// <summary>The agent wants to run a tool: show the question in the conversation and wait for a button.</summary>
    private async Task<string?> AskPermissionAsync(AcpPermissionRequest request)
    {
        if (_session?.Id != request.SessionId)
            return null;
        var item = new PermissionItem(request);
        _pendingPermissions.Add(item);
        Items.Add(item);
        try
        {
            return await item.Answer;
        }
        finally
        {
            _pendingPermissions.Remove(item);
        }
    }

    private void CancelPendingPermissions()
    {
        foreach (var item in _pendingPermissions.ToList())
            item.Cancel();
    }
}
