using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AXamlIDE.Services.Testing;

namespace AXamlIDE.Services.Mcp;

/// <summary>What the IDE's panels have collected: the Output pane (build log, the running app's console) and the Tests panel.</summary>
public sealed partial class IdeMcpServer
{
    // ------------------------------------------------------------ get_output

    private McpTool GetOutput()
    {
        var schema = McpArguments.Schema(
            ("pane", "string", "build: the last build's log. console: what the app the user ran or is debugging printed " +
                               "(stdout, stderr, and the debugger's messages).", ["build", "console"]),
            ("lines", "number", "The last this many lines. Default 80.", null),
            ("contains", "string", "Only lines containing this text (case-insensitive), e.g. an exception's name.", null));
        schema["required"] = new JsonArray("pane");
        return new McpTool("get_output",
            "The IDE's Output pane: the build log or the running app's console output, newest last.",
            schema,
            (arguments, _) => OnUiThread(() => ReadOutput(arguments)));
    }

    private McpToolResult ReadOutput(JsonElement arguments)
    {
        var console = McpArguments.String(arguments, "pane") == "console";
        IEnumerable<string> lines = console ? _run.ConsoleOutput : _run.BuildOutput;
        if (McpArguments.String(arguments, "contains") is { Length: > 0 } contains)
            lines = lines.Where(line => line.Contains(contains, StringComparison.OrdinalIgnoreCase));
        var all = lines.ToList();
        var count = (int)Math.Clamp(McpArguments.Number(arguments, "lines") ?? 80, 1, 1000);
        if (all.Count == 0)
            return McpToolResult.Text(console ? "No console output (nothing has been run this session)." : "No build output yet.");

        var text = new StringBuilder();
        if (all.Count > count)
            text.AppendLine($"(the last {count} of {all.Count} lines)");
        foreach (var line in all.Skip(Math.Max(0, all.Count - count)))
            text.AppendLine(line.Length <= 500 ? line : line[..499] + "…");
        if (console)
            text.AppendLine(_run.IsRunning ? "(the app is still running)" : "(the app isn't running)");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ get_test_results

    private McpTool GetTestResults()
    {
        var schema = McpArguments.Schema(
            ("outcome", "string", "Which tests to list. Default: failed.", ["failed", "all"]),
            ("limit", "number", "At most this many tests. Default 20.", null));
        return new McpTool("get_test_results",
            "The Tests panel's last run: the counts, then each failed test with its message and stack trace (or every " +
            "test with its outcome). To run tests yourself, use dotnet test; this shows what the user ran.",
            schema,
            (arguments, _) => OnUiThread(() => ReadTestResults(arguments)));
    }

    private McpToolResult ReadTestResults(JsonElement arguments)
    {
        var leaves = _tests.Roots.SelectMany(root => root.Leaves()).ToList();
        if (leaves.Count == 0)
            return McpToolResult.Text("The Tests panel has no tests (it hasn't discovered or run any).");

        var text = new StringBuilder();
        text.AppendLine($"{_tests.PassedCount} passed, {_tests.FailedCount} failed, {_tests.SkippedCount} skipped, {_tests.NotRunCount} not run" +
                        (_tests.IsBusy ? " (a run is going)" : "") + ".");
        var all = McpArguments.String(arguments, "outcome") == "all";
        var limit = (int)Math.Clamp(McpArguments.Number(arguments, "limit") ?? 20, 1, 500);
        var listed = leaves.Where(test => all || test.Outcome == TestOutcome.Failed).ToList();

        foreach (var test in listed.Take(limit))
        {
            var name = test.Test?.FullName ?? test.Result?.Name.FullName ?? test.Name;
            text.AppendLine($"{test.OutcomeText}: {name}{(test.DurationText.Length > 0 ? $" ({test.DurationText})" : "")}");
            if (test.Outcome != TestOutcome.Failed || test.Result is not { } result)
                continue;
            if (result.ErrorMessage is { Length: > 0 } message)
                text.AppendLine("  " + message.Trim().ReplaceLineEndings("\n  "));
            if (result.StackTrace is { Length: > 0 } stack)
                text.AppendLine("  " + string.Join("\n  ", stack.Trim().ReplaceLineEndings("\n").Split('\n').Take(15)));
        }
        if (listed.Count > limit)
            text.AppendLine($"...and {listed.Count - limit} more.");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }
}
