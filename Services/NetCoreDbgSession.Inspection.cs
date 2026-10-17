using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services;

public sealed record DebugFrame(int Id, string Name, string? Path, int Line);

public sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference);

// NOTE: change the declaration in NetCoreDbgSession.cs to `public sealed partial class NetCoreDbgSession`
public sealed partial class NetCoreDbgSession
{
    public Task StepOverAsync(int threadId, CancellationToken ct = default) => SendAsync("next", new { threadId }, ct);
    public Task StepIntoAsync(int threadId, CancellationToken ct = default) => SendAsync("stepIn", new { threadId }, ct);
    public Task StepOutAsync(int threadId, CancellationToken ct = default) => SendAsync("stepOut", new { threadId }, ct);
    public Task PauseAsync(int threadId, CancellationToken ct = default) => SendAsync("pause", new { threadId }, ct);

    private async Task SendAsync(string command, object args, CancellationToken ct)
    {
        using var _ = await RequestAsync(command, args, ct);
    }

    public async Task<IReadOnlyList<DebugFrame>> GetStackAsync(int threadId, int levels = 50, CancellationToken ct = default)
    {
        using var r = await RequestAsync("stackTrace", new { threadId, startFrame = 0, levels }, ct);
        var list = new List<DebugFrame>();
        foreach (var f in r.RootElement.GetProperty("body").GetProperty("stackFrames").EnumerateArray())
        {
            string? path = f.TryGetProperty("source", out var s) && s.TryGetProperty("path", out var p)
                ? p.GetString()
                : null;
            list.Add(new DebugFrame(
                f.GetProperty("id").GetInt32(),
                f.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                path,
                f.TryGetProperty("line", out var l) ? l.GetInt32() : 0));
        }

        return list;
    }

    /// <summary>Variables of every non-expensive scope of a frame (netcoredbg gives one "Locals" scope, including `this`).</summary>
    public async Task<IReadOnlyList<DebugVariable>> GetLocalsAsync(int frameId, CancellationToken ct = default)
    {
        using var r = await RequestAsync("scopes", new { frameId }, ct);
        var refs = new List<int>();
        foreach (var scope in r.RootElement.GetProperty("body").GetProperty("scopes").EnumerateArray())
        {
            if (scope.TryGetProperty("expensive", out var exp) && exp.ValueKind == JsonValueKind.True)
                continue;
            refs.Add(scope.GetProperty("variablesReference").GetInt32());
        }

        var all = new List<DebugVariable>();
        foreach (var reference in refs)
            all.AddRange(await GetVariablesAsync(reference, ct));
        return all;
    }

    public async Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        using var r = await RequestAsync("variables", new { variablesReference }, ct);
        var list = new List<DebugVariable>();
        foreach (var v in r.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray())
            list.Add(ToVariable(v));
        return list;
    }

    /// <summary>For the hover tooltip later: context "hover" avoids side effects where the adapter supports it.</summary>
    public async Task<DebugVariable?> EvaluateAsync(int frameId, string expression, string context = "hover",
        CancellationToken ct = default)
    {
        try
        {
            using var r = await RequestAsync("evaluate", new { expression, frameId, context }, ct);
            var body = r.RootElement.GetProperty("body");
            return new DebugVariable(
                expression,
                body.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "",
                body.TryGetProperty("type", out var t) ? t.GetString() : null,
                body.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0);
        }
        catch (InvalidOperationException)
        {
            return null; // evaluation failed (e.g. not an expression)
        }
    }

    public async Task<string?> GetExceptionTextAsync(int threadId, CancellationToken ct = default)
    {
        try
        {
            using var r = await RequestAsync("exceptionInfo", new { threadId }, ct);
            var b = r.RootElement.GetProperty("body");
            var id = b.TryGetProperty("exceptionId", out var i) ? i.GetString() : null;
            var desc = b.TryGetProperty("description", out var d) ? d.GetString() : null;
            string? stack = null;
            if (b.TryGetProperty("details", out var det) && det.TryGetProperty("stackTrace", out var st))
                stack = st.GetString();
            return $"{id}: {desc}{(stack is null ? "" : Environment.NewLine + stack)}";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DebugVariable ToVariable(JsonElement v) => new(
        v.GetProperty("name").GetString() ?? "?",
        v.TryGetProperty("value", out var val) ? val.GetString() ?? "" : "",
        v.TryGetProperty("type", out var t) ? t.GetString() : null,
        v.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0);
}
