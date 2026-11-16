using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Agents;

// The Agent Client Protocol's messages, as the IDE uses them (https://agentclientprotocol.com/protocol/schema).
// Only the fields the chat panel shows are read; anything else in the JSON is ignored, so a newer agent
// that sends more still works.

/// <summary>One thing the agent reports during a turn (a session/update notification).</summary>
public abstract record AcpUpdate;

public enum AcpChunkKind
{
    /// <summary>The agent's reply.</summary>
    Agent,

    /// <summary>The agent's thinking (shown dimmed, collapsed).</summary>
    Thought,

    /// <summary>The user's own message, sent back when a session is loaded.</summary>
    User
}

/// <summary>
/// A piece of a message, as it streams in. Pieces with the same <see cref="MessageId"/> belong to one message;
/// a new id starts a new one. Agents that send no id mean "the message being written".
/// </summary>
public sealed record AcpMessageChunk(AcpChunkKind Kind, string Text, string? MessageId) : AcpUpdate;

/// <summary>
/// A tool call starting (<see cref="IsNew"/>) or changing. Only the fields that changed are set; the rest are null,
/// so merge it into what's already known. <see cref="Content"/> and <see cref="Locations"/> replace the old lists.
/// </summary>
public sealed record AcpToolCall(
    string Id,
    bool IsNew,
    string? Title,
    string? Kind,
    string? Status,
    IReadOnlyList<AcpToolContent>? Content,
    IReadOnlyList<AcpLocation>? Locations) : AcpUpdate;

/// <summary>The agent's to-do list for the task. Each update is the whole list.</summary>
public sealed record AcpPlan(IReadOnlyList<AcpPlanEntry> Entries) : AcpUpdate;

/// <param name="Status">pending, in_progress or completed.</param>
public sealed record AcpPlanEntry(string Content, string Status);

/// <summary>The session's settings and their choices changed (the permission mode, the model).</summary>
public sealed record AcpConfigOptionsChanged(IReadOnlyList<AcpConfigOption> Options) : AcpUpdate;

/// <summary>The agent gave the session a title ("Reverse hello.txt text").</summary>
public sealed record AcpSessionTitle(string Title) : AcpUpdate;

/// <summary>How much of the context window is used, in tokens.</summary>
public sealed record AcpUsage(long Used, long Size) : AcpUpdate;

/// <summary>What a tool call shows: some text (Markdown), a file's change, or a terminal.</summary>
public abstract record AcpToolContent;

public sealed record AcpTextContent(string Text) : AcpToolContent;

/// <summary>A file the tool changes. <see cref="OldText"/> is null for a new file. The texts may be just the changed part.</summary>
public sealed record AcpDiffContent(string Path, string? OldText, string NewText) : AcpToolContent;

/// <summary>A file (and line, 1-based) a tool call is working on, for "follow along".</summary>
public sealed record AcpLocation(string Path, int? Line);

/// <summary>
/// A setting the agent offers for the session, with its choices: "mode" (Manual, Accept edits, Plan, Auto...) and
/// "model" from claude-agent-acp. Set one with <see cref="AcpClient.SetConfigOptionAsync"/>.
/// </summary>
public sealed record AcpConfigOption(string Id, string Name, string? Category, string CurrentValue, IReadOnlyList<AcpChoice> Choices);

public sealed record AcpChoice(string Value, string Name, string? Description);

/// <summary>A session the agent has started: its id and the settings it offers.</summary>
public sealed record AcpSession(string Id, IReadOnlyList<AcpConfigOption> Options);

/// <summary>
/// A past conversation, from session/list: its id, title (the agent's name for it; may be null) and when it last
/// changed. Claude Code's sessions are shared, so this includes ones started in VS Code or the terminal.
/// </summary>
public sealed record AcpSessionInfo(string Id, string? Title, System.DateTimeOffset? UpdatedAt);

/// <summary>
/// The agent asks before running a tool (session/request_permission). The answer is one of the
/// <see cref="Options"/>' ids, or null for "cancelled" (the turn was stopped).
/// </summary>
public sealed record AcpPermissionRequest(string SessionId, AcpToolCall ToolCall, IReadOnlyList<AcpPermissionOption> Options);

/// <param name="Kind">allow_once, allow_always, reject_once or reject_always.</param>
public sealed record AcpPermissionOption(string Id, string Name, string Kind)
{
    public bool Allows => Kind.StartsWith("allow", System.StringComparison.Ordinal);
}

/// <summary>Reads the protocol's JSON into the records above. Unknown or malformed parts become null, never an exception.</summary>
public static class AcpParser
{
    /// <summary>A session/update notification's "update" object, or null for kinds the IDE doesn't show.</summary>
    public static AcpUpdate? ParseUpdate(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object)
            return null;

        return String(update, "sessionUpdate") switch
        {
            "agent_message_chunk" => Chunk(AcpChunkKind.Agent, update),
            "agent_thought_chunk" => Chunk(AcpChunkKind.Thought, update),
            "user_message_chunk" => Chunk(AcpChunkKind.User, update),
            "tool_call" => ToolCall(update, isNew: true),
            "tool_call_update" => ToolCall(update, isNew: false),
            "plan" => Plan(update),
            "config_option_update" => new AcpConfigOptionsChanged(ConfigOptions(update)),
            "session_info_update" => String(update, "title") is { Length: > 0 } title ? new AcpSessionTitle(title) : null,
            "usage_update" => update.TryGetProperty("used", out var used) && used.TryGetInt64(out var u)
                              && update.TryGetProperty("size", out var size) && size.TryGetInt64(out var s)
                ? new AcpUsage(u, s)
                : null,
            _ => null
        };
    }

    /// <summary>The "configOptions" array of a session/new response or a config_option_update.</summary>
    public static IReadOnlyList<AcpConfigOption> ConfigOptions(JsonElement owner)
    {
        if (!owner.TryGetProperty("configOptions", out var options) || options.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<AcpConfigOption>();
        foreach (var option in options.EnumerateArray())
        {
            // Only "select" settings (a list of choices); booleans and others aren't shown yet.
            if (String(option, "id") is not { } id || String(option, "currentValue") is not { } current
                || !option.TryGetProperty("options", out var choices) || choices.ValueKind != JsonValueKind.Array)
                continue;
            var parsed = choices.EnumerateArray()
                .Where(choice => String(choice, "value") is not null)
                .Select(choice => new AcpChoice(String(choice, "value")!, String(choice, "name") ?? String(choice, "value")!, String(choice, "description")))
                .ToList();
            list.Add(new AcpConfigOption(id, String(option, "name") ?? id, String(option, "category"), current, parsed));
        }
        return list;
    }

    /// <summary>A session/request_permission request's params, or null if they're malformed.</summary>
    public static AcpPermissionRequest? PermissionRequest(JsonElement @params)
    {
        if (String(@params, "sessionId") is not { } sessionId
            || !@params.TryGetProperty("toolCall", out var toolCall)
            || ToolCall(toolCall, isNew: false) is not { } call
            || !@params.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        var parsed = options.EnumerateArray()
            .Where(option => String(option, "optionId") is not null)
            .Select(option => new AcpPermissionOption(String(option, "optionId")!, String(option, "name") ?? String(option, "optionId")!, String(option, "kind") ?? ""))
            .ToList();
        return new AcpPermissionRequest(sessionId, call, parsed);
    }

    private static AcpMessageChunk? Chunk(AcpChunkKind kind, JsonElement update)
    {
        // Only text is shown; images and other blocks in a message are skipped.
        if (!update.TryGetProperty("content", out var content) || String(content, "type") != "text" || String(content, "text") is not { } text)
            return null;
        return new AcpMessageChunk(kind, text, String(update, "messageId"));
    }

    private static AcpToolCall? ToolCall(JsonElement update, bool isNew)
    {
        if (String(update, "toolCallId") is not { } id)
            return null;

        IReadOnlyList<AcpToolContent>? content = null;
        if (update.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
            content = [.. contentArray.EnumerateArray().Select(ToolContent).OfType<AcpToolContent>()];

        IReadOnlyList<AcpLocation>? locations = null;
        if (update.TryGetProperty("locations", out var locationArray) && locationArray.ValueKind == JsonValueKind.Array)
            locations =
            [
                .. locationArray.EnumerateArray()
                    .Where(location => String(location, "path") is not null)
                    .Select(location => new AcpLocation(String(location, "path")!,
                        location.TryGetProperty("line", out var line) && line.TryGetInt32(out var number) ? number : null))
            ];

        return new AcpToolCall(id, isNew, String(update, "title"), String(update, "kind"), String(update, "status"), content, locations);
    }

    private static AcpToolContent? ToolContent(JsonElement item)
    {
        switch (String(item, "type"))
        {
            case "content":
                return item.TryGetProperty("content", out var block) && String(block, "type") == "text" && String(block, "text") is { } text
                    ? new AcpTextContent(text)
                    : null;
            case "diff":
                return String(item, "path") is { } path && String(item, "newText") is { } newText
                    ? new AcpDiffContent(path, String(item, "oldText"), newText)
                    : null;
            default:
                return null;   // "terminal": the IDE doesn't offer terminals to the agent, so none are sent
        }
    }

    private static AcpPlan Plan(JsonElement update)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new AcpPlan([]);
        return new AcpPlan(
        [
            .. entries.EnumerateArray()
                .Where(entry => String(entry, "content") is not null)
                .Select(entry => new AcpPlanEntry(String(entry, "content")!, String(entry, "status") ?? "pending"))
        ]);
    }

    /// <summary>The string property, or null when it's missing or not a string.</summary>
    /// <summary>A session/list result: the sessions on this page, and the cursor for the next page (null on the last).</summary>
    public static (IReadOnlyList<AcpSessionInfo> Sessions, string? NextCursor) SessionList(JsonElement result)
    {
        var sessions = new List<AcpSessionInfo>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var session in list.EnumerateArray())
            {
                if (String(session, "sessionId") is not { } id)
                    continue;
                var updated = System.DateTimeOffset.TryParse(String(session, "updatedAt"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when : (System.DateTimeOffset?)null;
                sessions.Add(new AcpSessionInfo(id, String(session, "title"), updated));
            }
        }
        return (sessions, String(result, "nextCursor"));
    }

    internal static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
