using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Debugging;

public sealed record StopSnapshot(
    string Reason,
    int ThreadId,
    IReadOnlyList<DebugFrame> Frames,
    IReadOnlyDictionary<string, string> Values,
    string? ExceptionText)
{
    /// <summary>First frame that has source (justMyCode already hides framework frames).</summary>
    public DebugFrame? Top => Frames.FirstOrDefault(f => f.Path is not null);
}

/// <summary>
/// Listens to NetCoreDbgSession events. On "stopped" it gathers the stack, locals and `this` members,
/// writes the stack trace to the output pane, and raises <see cref="Stopped"/> (on the thread pool - marshal to UI yourself).
/// </summary>
public sealed class DebugInspector
{
    private readonly NetCoreDbgSession _session;

    public int ThreadId { get; private set; }
    public StopSnapshot? Current { get; private set; }

    public event Action<StopSnapshot>? Stopped;
    public event Action? Resumed;
    public event Action<string>? Output;

    public DebugInspector(NetCoreDbgSession session)
    {
        _session = session;
        session.EventReceived += OnEvent;
    }

    public void Detach() => _session.EventReceived -= OnEvent;

    private void OnEvent(JsonElement e)
    {
        switch (e.GetProperty("event").GetString())
        {
            case "stopped" when e.TryGetProperty("body", out var body):
                // Fire and forget: awaiting DAP requests here would block the message pump.
                _ = HandleStoppedAsync(body);
                break;
            case "continued":
                Current = null;
                Resumed?.Invoke();
                break;
        }
    }

    private async Task HandleStoppedAsync(JsonElement body)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = cts.Token;

            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var threadId = body.TryGetProperty("threadId", out var t) ? t.GetInt32() : 0;
            ThreadId = threadId;

            var frames = await _session.GetStackAsync(threadId, ct: ct);
            var values = new Dictionary<string, string>();
            var top = frames.FirstOrDefault(f => f.Path is not null);
            if (top is not null)
                await CollectValuesAsync(top.Id, values, ct);

            var exception = reason == "exception" ? await _session.GetExceptionTextAsync(threadId, ct) : null;

            var snapshot = new StopSnapshot(reason, threadId, frames, values, exception);
            Current = snapshot;
            Output?.Invoke(FormatStop(snapshot));
            Stopped?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Output?.Invoke($"[debug] Could not inspect stop: {ex.Message}");
        }
    }

    private async Task CollectValuesAsync(int frameId, Dictionary<string, string> values, CancellationToken ct)
    {
        var locals = await _session.GetLocalsAsync(frameId, ct);
        foreach (var v in locals)
            if (v.Name != "this" && v.VariablesReference == 0)
                values.TryAdd(v.Name, Shorten(v.Value)); // locals win over members of the same name

        // One level of `this`: primitives/strings only, so we never expand big object graphs
        // (note: property expansion runs getters inside the debuggee).
        var self = locals.FirstOrDefault(v => v.Name == "this");
        if (self is { VariablesReference: > 0 })
            foreach (var m in await _session.GetVariablesAsync(self.VariablesReference, ct))
                if (m.VariablesReference == 0)
                    values.TryAdd(m.Name, Shorten(m.Value));
    }

    private static string Shorten(string value) =>
        value.Length <= 40 ? value : value[..39] + "…";

    private static string FormatStop(StopSnapshot s)
    {
        var sb = new StringBuilder();
        var top = s.Top;
        sb.Append("[debug] Paused (").Append(s.Reason).Append(')');
        if (top is not null)
            sb.Append(" at ").Append(System.IO.Path.GetFileName(top.Path)).Append(':').Append(top.Line);
        sb.AppendLine();
        if (s.ExceptionText is not null)
            sb.AppendLine(s.ExceptionText);
        foreach (var f in s.Frames)
            sb.Append("   at ").Append(f.Name)
              .Append(f.Path is null ? "" : $" in {f.Path}:line {f.Line}")
              .AppendLine();
        return sb.ToString().TrimEnd();
    }
}
