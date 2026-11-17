using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Agents;

/// <summary>
/// JSON-RPC plumbing: the outgoing queue, matching responses to requests, the read and write loops, and what to do
/// with each message the agent sends. Unlike LSP there are no Content-Length headers: each message is one line of
/// JSON (UTF-8, no newlines inside it), ended by '\n'.
/// </summary>
public sealed partial class AcpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Stream _fromAgent;
    private readonly Stream _toAgent;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private volatile bool _disposed;

    // The last few lines of the agent's log, so an agent that dies at start-up can say why.
    private readonly Queue<string> _recentLog = new();

    public string RecentLog { get { lock (_recentLog) return string.Join(" | ", _recentLog); } }

    private void Log(string line)
    {
        lock (_recentLog)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > 8)
                _recentLog.Dequeue();
        }
        AgentLog?.Invoke(line);
    }

    // ------------------------------------------------------------- sending

    private void Notify(string method, object? @params) => Enqueue(new { jsonrpc = "2.0", method, @params });

    private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        // ACP has no $/cancelRequest: giving up only stops waiting (a turn is stopped with session/cancel).
        await using var registration = ct.Register(() => reply.TrySetCanceled(ct));
        Enqueue(new { jsonrpc = "2.0", id, method, @params });

        try
        {
            return await reply.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Answer a request from the agent. Its id is sent back exactly as it came (a number or a string).</summary>
    private void Respond(JsonElement id, object result) => Enqueue(new { jsonrpc = "2.0", id, result });

    private void RespondError(JsonElement id, int code, string message)
        => Enqueue(new { jsonrpc = "2.0", id, error = new { code, message } });

    private void Enqueue(object message)
    {
        if (_disposed)
            return;
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);   // never contains a raw newline
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        _outbox.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _toAgent.WriteAsync(line, _cts.Token);
                await _toAgent.FlushAsync(_cts.Token);
            }
        }
        catch (Exception) { /* agent gone, or disposing */ }
    }

    // ------------------------------------------------------------- receiving

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_fromAgent, new UTF8Encoding(false));
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                    return;   // EOF: the agent exited
                if (line.Length == 0)
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    Dispatch(document.RootElement);
                }
                catch (JsonException)
                {
                    Log("Not JSON: " + (line.Length > 200 ? line[..200] + "..." : line));   // stray output on stdout
                }
            }
        }
        catch (Exception) { /* EOF, or cancelled */ }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new AcpException("The agent stopped."));
        }
    }

    private void Dispatch(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var id);
        var method = AcpParser.String(message, "method");

        // A response to one of our requests.
        if (method is null)
        {
            if (hasId && id.TryGetInt32(out var number) && _pending.TryRemove(number, out var reply))
            {
                if (message.TryGetProperty("error", out var error))
                    reply.TrySetException(new AcpException(AcpParser.String(error, "message") ?? "The agent returned an error.",
                        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : null));
                else
                    reply.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
            return;
        }

        var @params = message.TryGetProperty("params", out var p) ? p.Clone() : default;
        if (hasId)
            OnRequest(id.Clone(), method, @params);
        else
            OnNotification(method, @params);
    }

    private void OnNotification(string method, JsonElement @params)
    {
        switch (method)
        {
            case "session/update":
                if (AcpParser.String(@params, "sessionId") is { } sessionId
                    && @params.TryGetProperty("update", out var update)
                    && AcpParser.ParseUpdate(update) is { } parsed)
                    Updated?.Invoke(sessionId, parsed);
                break;

            case "auth/status_update":   // claude-agent-acp's own: whether it's signed in
                if (AcpParser.String(@params, "state") is { } state && state != AuthState)
                {
                    AuthState = state;
                    AuthStateChanged?.Invoke(state);
                }
                break;
        }
    }

    private void OnRequest(JsonElement id, string method, JsonElement @params)
    {
        switch (method)
        {
            case "session/request_permission":
                _ = AnswerPermissionAsync(id, @params);
                break;

            default:
                RespondError(id, -32601, $"AXamlIDE doesn't support {method}.");   // JSON-RPC's "method not found"
                break;
        }
    }

    private async Task AnswerPermissionAsync(JsonElement id, JsonElement @params)
    {
        string? optionId = null;
        try
        {
            if (AcpParser.PermissionRequest(@params) is { } request && PermissionHandler is { } handler)
                optionId = await handler(request);
        }
        catch (Exception ex)
        {
            Log("Permission handler failed: " + ex.Message);
        }

        Respond(id, optionId is null
            ? new { outcome = (object)new { outcome = "cancelled" } }
            : new { outcome = (object)new { outcome = "selected", optionId } });
    }
}
