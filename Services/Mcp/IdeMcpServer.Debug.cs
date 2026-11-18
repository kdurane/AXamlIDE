using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Debugging;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// The user's debugger while it's paused: why it stopped, the call stack and the locals (get_debug_state), and any
/// expression evaluated where it stopped (evaluate_expression). Read through the same DebugValueProvider as the
/// Locals and Watch windows, so values look as they do there ("Count = 3", [DebuggerDisplay]).
/// </summary>
public sealed partial class IdeMcpServer
{
    /// <summary>Children listed per value; the rest are counted.</summary>
    private const int MaxChildren = 25;

    /// <summary>A value's display is cut to this.</summary>
    private const int MaxDisplay = 200;

    private static readonly (string, string, string, string[]?) FrameArgument =
        ("frame", "number", "Which call stack frame, 0 = where it stopped. Default: the one selected in the Call Stack window.", null);

    private static readonly (string, string, string, string[]?) DepthArgument =
        ("depth", "number", "How many levels of members to expand (0-3). Default 1.", null);

    // ------------------------------------------------------------ get_debug_state

    private McpTool GetDebugState() => new("get_debug_state",
        "The user's debug session: whether it's paused; if so why (breakpoint, step, exception with its message), the " +
        "call stack (frame number, method, file:line), the code line it's on, and the frame's locals with their members, " +
        "as the Locals window shows them. Valid until the user continues.",
        McpArguments.Schema(FrameArgument, DepthArgument),
        (arguments, ct) => OnUiThread(() => DebugStateAsync(arguments, ct)));

    private async Task<McpToolResult> DebugStateAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!_run.IsDebugging)
            return McpToolResult.Text("Not debugging.");
        if (!_run.IsDebugStopped || _debug.Current is not { } stop)
            return McpToolResult.Text("Debugging, and the app is running (not paused). The user can pause it or hit a breakpoint.");
        if (FrameOf(arguments, stop) is not { } frame)
            return McpToolResult.Error($"There are {stop.Frames.Count} frames (0 to {stop.Frames.Count - 1}).");

        var text = new StringBuilder();
        text.AppendLine($"Paused: {stop.Reason}.");
        if (stop.ExceptionText is { Length: > 0 } exception)
            text.AppendLine($"Exception: {exception.Trim()}");

        text.AppendLine("Call stack:");
        for (var i = 0; i < stop.Frames.Count; i++)
        {
            var f = stop.Frames[i];
            var where = f.Path is { } path ? $" {Relative(path)}:{f.Line}" : " [external]";
            text.AppendLine($"{(f == frame ? ">" : " ")} {i} {f.Name}{where}");
        }

        if (frame.Path is { } source && await TextOfAsync(source, ct) is { } code)
        {
            var lines = code.ReplaceLineEndings("\n").Split('\n');
            if (frame.Line >= 1 && frame.Line <= lines.Length)
                text.AppendLine($"At line {frame.Line}: {lines[frame.Line - 1].Trim()}");
        }

        var values = new DebugValueProvider(stop.Debugger, frame.Id, stop.Attributes);
        var depth = DepthOf(arguments);
        try
        {
            var locals = await values.LocalsAsync(ct);
            text.AppendLine(locals.Count == 0 ? "No locals." : "Locals:");
            foreach (var local in locals)
                await AppendValueAsync(text, values, local, 1, depth, ct);
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
            text.AppendLine($"Couldn't read the locals: {ex.Message}");
        }
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ evaluate_expression

    private McpTool EvaluateExpression()
    {
        var schema = McpArguments.Schema(
            ("expression", "string", "A C# expression, as typed in the Watch window (person.Name, items.Count, x > 3).", null),
            FrameArgument, DepthArgument);
        schema["required"] = new JsonArray("expression");
        return new McpTool("evaluate_expression",
            "Evaluate a C# expression where the user's debugger is paused (like the Watch window) and show its value and " +
            "members. It runs in the user's app: property getters and method calls really run, so avoid ones that change state.",
            schema,
            (arguments, ct) => OnUiThread(() => EvaluateAsync(arguments, ct)));
    }

    private async Task<McpToolResult> EvaluateAsync(JsonElement arguments, CancellationToken ct)
    {
        if (McpArguments.String(arguments, "expression") is not { Length: > 0 } expression)
            return McpToolResult.Error("Give an expression.");
        if (!_run.IsDebugStopped || _debug.Current is not { } stop)
            return McpToolResult.Error(_run.IsDebugging ? "The app is running; it can only be evaluated while paused." : "Not debugging.");
        if (FrameOf(arguments, stop) is not { } frame)
            return McpToolResult.Error($"There are {stop.Frames.Count} frames (0 to {stop.Frames.Count - 1}).");

        var values = new DebugValueProvider(stop.Debugger, frame.Id, stop.Attributes);
        try
        {
            var value = await values.EvaluateAsync(expression, "watch", ct);
            if (value.Kind == DebugValueKind.Error)
                return McpToolResult.Error($"{expression}: {value.Display}");
            var text = new StringBuilder();
            await AppendValueAsync(text, values, value, 0, DepthOf(arguments), ct);
            return McpToolResult.Text(text.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
            return McpToolResult.Error($"Couldn't evaluate {expression}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ helpers

    private DebugFrame? FrameOf(JsonElement arguments, StopSnapshot stop)
    {
        if (McpArguments.Number(arguments, "frame") is not { } index)
            return _debug.SelectedFrame ?? stop.Top ?? stop.Frames.FirstOrDefault();
        var i = (int)index;
        return i >= 0 && i < stop.Frames.Count ? stop.Frames[i] : null;
    }

    private static int DepthOf(JsonElement arguments) => (int)Math.Clamp(McpArguments.Number(arguments, "depth") ?? 1, 0, 3);

    /// <summary>"name = display (type)", then its children indented, down to <paramref name="depth"/> levels below the top.</summary>
    private static async Task AppendValueAsync(StringBuilder text, DebugValueProvider values, DebugValue value, int indent, int depth,
        CancellationToken ct)
    {
        var display = value.Display.ReplaceLineEndings(" ");
        if (display.Length > MaxDisplay)
            display = display[..(MaxDisplay - 1)] + "…";
        text.Append(' ', indent * 2).Append(value.Name);
        if (value.Kind != DebugValueKind.Group)
            text.Append(" = ").Append(display);
        if (value.Type is { Length: > 0 } type && value.Kind != DebugValueKind.Group)
            text.Append(" (").Append(type).Append(')');
        text.AppendLine();

        // Groups (Raw View, Static members, Non-Public members) are left closed, as in VS, to keep it short.
        if (!value.HasChildren || depth <= 0 || value.Kind == DebugValueKind.Group)
            return;
        IReadOnlyList<DebugValue> children;
        try
        {
            children = await values.ChildrenAsync(value, ct);
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
            text.Append(' ', (indent + 1) * 2).AppendLine($"(couldn't read: {ex.Message})");
            return;
        }
        foreach (var child in children.Take(MaxChildren))
            await AppendValueAsync(text, values, child, indent + 1, depth - 1, ct);
        if (children.Count > MaxChildren)
            text.Append(' ', (indent + 1) * 2).AppendLine($"...and {children.Count - MaxChildren} more");
    }
}
