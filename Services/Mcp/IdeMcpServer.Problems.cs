using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Mcp;

/// <summary>Is it right? The Problems list, one file checked now (check_file), and the IDE's build.</summary>
public sealed partial class IdeMcpServer
{
    // ------------------------------------------------------------ get_problems

    private McpTool GetProblems()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "Only this file's problems (absolute or workspace-relative). Default: every file.", null),
            ("severity", "string", "The least severe to include. Default: warning.", ["error", "warning", "info"]),
            ("limit", "number", "At most this many lines. Default 50.", null));
        return new McpTool("get_problems",
            "The IDE's Problems list (language server, the last build, the previewer), one per line: " +
            "file:line:column severity code message [source]. Starts with what it covers (when the last build was, " +
            "whether closed files are analysed), since an empty list may only mean nothing has been checked. " +
            "For one file just edited, check_file is fresher.",
            schema,
            (arguments, _) => OnUiThread(() => ListProblems(arguments)));
    }

    private McpToolResult ListProblems(JsonElement arguments)
    {
        var path = ResolvePath(McpArguments.String(arguments, "path"));
        var least = SeverityArgument(arguments);
        var limit = (int)Math.Clamp(McpArguments.Number(arguments, "limit") ?? 50, 1, 500);

        var problems = _problems.All
            .Where(p => p.Severity <= least)
            .Where(p => path is null || p.Path.Length > 0 && GitStatus.PathComparer.Equals(Path.GetFullPath(p.Path), path))
            .OrderBy(p => p.Severity).ThenBy(p => p.Path).ThenBy(p => p.Line)
            .ToList();

        var text = new StringBuilder();
        text.AppendLine(Coverage());
        if (problems.Count == 0)
            text.AppendLine(path is null ? "No problems." : $"No problems in {Relative(path)}.");
        foreach (var p in problems.Take(limit))
        {
            var where = p.Path.Length == 0 ? "(project)" : p.Line > 0 ? $"{Relative(p.Path)}:{p.Line}:{p.Column}" : Relative(p.Path);
            var code = p.Code.Length > 0 ? p.Code + " " : "";
            text.Append(where).Append(' ').Append(SeverityName(p.Severity)).Append(' ').Append(code)
                .Append(p.Message.ReplaceLineEndings(" ")).Append(" [").Append(p.Source).Append(']').AppendLine();
        }
        if (problems.Count > limit)
            text.AppendLine($"...and {problems.Count - limit} more.");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    /// <summary>What the list is made of right now, in a line or two.</summary>
    private string Coverage()
    {
        var build = _run.LastBuild is { } last
            ? $"last build {Ago(last.Finished)} {(last.Succeeded ? "succeeded" : "failed")} ({last.Errors} errors, {last.Warnings} warnings)"
            : "not built this session";
        var analysis = _solutionAnalysis.IsEnabled
            ? _solutionAnalysis.HasResults ? "every file analysed (solution-wide analysis)" : "solution-wide analysis still starting"
            : "only open files analysed (solution-wide analysis is off)";
        var servers = _servers.Summary;
        var busy = servers.BusyText is { } doing ? $"; language server busy: {doing}" : "";
        var broken = servers.ProblemText is { } problem ? $"; {problem}" : "";
        return $"Covers: {build}; {analysis}{busy}{broken}.";
    }

    private static string Ago(DateTime time)
    {
        var age = DateTime.Now - time;
        return age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s ago"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : $"at {time:HH:mm}";
    }

    private static int SeverityArgument(JsonElement arguments) => McpArguments.String(arguments, "severity") switch
    {
        "error" => LspDiagnosticSeverity.Error,
        "info" => LspDiagnosticSeverity.Hint,
        _ => LspDiagnosticSeverity.Warning
    };

    private static string SeverityName(int severity) => severity switch
    {
        LspDiagnosticSeverity.Error => "error",
        LspDiagnosticSeverity.Warning => "warning",
        _ => "info"
    };

    // ------------------------------------------------------------ check_file

    private McpTool CheckFile()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "The .cs file (absolute or workspace-relative). Default: the active one.", null),
            ("severity", "string", "The least severe to include. Default: warning.", ["error", "warning", "info"]));
        return new McpTool("check_file",
            "Ask the C# language server for one file's errors and warnings now (its saved text, or the editor's when " +
            "it's open), without a build: a few seconds after an edit instead of a full build. Includes what the " +
            "edit broke in that file, not in the files that use it (find_references or build for those). " +
            "For .axaml files, render_xaml reports XAML errors.",
            schema,
            (arguments, ct) => OnUiThread(() => CheckFileAsync(arguments, ct)));
    }

    private async Task<McpToolResult> CheckFileAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = ResolvePath(McpArguments.String(arguments, "path")) ?? _documents.Active?.Path;
        if (path is null)
            return McpToolResult.Error("Give the path of a .cs file.");
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Error($"check_file reads C# files; for {Relative(path)} use get_problems" +
                                       (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ? " or render_xaml." : "."));
        if (!File.Exists(path) && FindOpen(path) is null)
            return McpToolResult.Error($"{Relative(path)} doesn't exist.");
        if (await CSharpClientAsync() is not { SupportsPullDiagnostics: true } client)
            return McpToolResult.Error("The C# language server isn't running or can't check single files; use build.");

        var least = SeverityArgument(arguments);
        var diagnostics = (await PullDiagnosticsAsync(client, path, ct))
            .Where(d => d.Severity <= least)
            .OrderBy(d => d.StartLine).ThenBy(d => d.StartChar)
            .ToList();

        var source = FindOpen(path) is { IsDirty: true } ? "the editor's unsaved text" : "the saved file";
        if (diagnostics.Count == 0)
            return McpToolResult.Text($"{Relative(path)} ({source}): no problems.");
        var text = new StringBuilder($"{Relative(path)} ({source}):").AppendLine();
        foreach (var d in diagnostics.Take(100))
        {
            var code = LspHelpers.DiagnosticCode(d.Raw);
            text.Append($"{d.StartLine + 1}:{d.StartChar + 1} {SeverityName(d.Severity)} ")
                .Append(code.Length > 0 ? code + " " : "").AppendLine(LspHelpers.DiagnosticText(d.Message, code).ReplaceLineEndings(" "));
        }
        if (diagnostics.Count > 100)
            text.AppendLine($"...and {diagnostics.Count - 100} more.");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    /// <summary>
    /// A file's diagnostics as they are now. Roslyn only reports on files the editor has open (it knows a closed file's
    /// text, but doesn't analyse it on request), so a closed file is opened with the server for the question, with
    /// its text from disk, and closed again; the user's editor isn't involved.
    /// </summary>
    private async Task<IReadOnlyList<LspDiagnostic>> PullDiagnosticsAsync(LspClient client, string path, CancellationToken ct)
    {
        var uri = FileUris.FromPath(path);
        if (FindOpen(path) is { } open)
        {
            _languages.ConnectionFor(open)?.Sync.Flush();   // the last keystrokes, if they're still waiting to be sent
            return await client.PullDiagnosticsAsync(uri, ct);
        }

        client.DidOpen(uri, LanguageIds.CSharp, 1, await File.ReadAllTextAsync(path, ct));
        try
        {
            return await client.PullDiagnosticsAsync(uri, ct);
        }
        finally
        {
            if (FindOpen(path) is null)   // the user opened it meanwhile: the editor has it with the server now
                client.DidClose(uri);
        }
    }

    // ------------------------------------------------------------ build

    private McpTool Build() => new("build",
        "Build the solution (else the startup project) the way the IDE's Build does, and wait for it: its errors land in " +
        "the user's Problems panel and the AXAML previews (render_xaml too) pick up the new build. Unsaved editor " +
        "changes aren't saved first and so aren't built. Returns the result and the first errors.",
        McpArguments.Schema(),
        (_, _) => OnUiThread(BuildAsync));

    private async Task<McpToolResult> BuildAsync()
    {
        var unsaved = _documents.Documents.Count(d => d.IsDirty && !d.IsReadOnly);
        if (await _run.BuildForAgentAsync() is not { } outcome)
            return McpToolResult.Error(_workspace.RootDirectory is null
                ? "No workspace is open."
                : "Couldn't start a build: one is already going (or the app is running), or there's no solution or startup project.");

        var text = new StringBuilder();
        text.Append(outcome.Succeeded ? "Build succeeded" : "Build failed")
            .Append($" ({outcome.Errors} errors, {outcome.Warnings} warnings).");
        if (unsaved > 0)
            text.Append($" {unsaved} open file{(unsaved == 1 ? " has" : "s have")} unsaved edits that weren't built.");
        text.AppendLine();
        foreach (var p in _problems.All.Where(p => p.Source == ProblemSource.Build && p.IsError).Take(30))
            text.AppendLine($"{(p.Path.Length == 0 ? "(project)" : $"{Relative(p.Path)}:{p.Line}:{p.Column}")} {p.Code} {p.Message.ReplaceLineEndings(" ")}");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    private Task<LspClient?> CSharpClientAsync() => _languages.ClientForLanguageAsync(LanguageIds.CSharp);
}
