using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.RoslynExtension;

namespace AXamlIDE.Services.Lsp;

/// <summary>One project's diagnostics from the extension, by full path, for the project <paramref name="Version"/>.</summary>
public sealed record RoslynProjectDiagnostics(
    string Version,
    IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> Files,
    int Checked,
    long Milliseconds);

/// <summary>
/// AXamlIDE's extension inside the Roslyn language server (Projects/AXamlIDE.RoslynExtension), loaded into one
/// server: it works on the server's live Solution directly, so it can say which projects changed and check just the
/// changed files, instead of Roslyn's workspace/diagnostic, which goes through every file of the solution each time.
/// </summary>
public sealed class RoslynExtensionClient
{
    private readonly LspClient _client;

    private RoslynExtensionClient(LspClient client) => _client = client;

    /// <summary>Where the build and publish put it: roslynextension/ beside the app.</summary>
    public static string AssemblyPath { get; } = Path.Combine(AppContext.BaseDirectory, "roslynextension", "AXamlIDE.RoslynExtension.dll");

    /// <summary>
    /// Load it into the server. Null, with the reason, if it isn't there (built without the server installed) or the
    /// server won't load it (an older server without extensions, or one whose extension API changed).
    /// </summary>
    public static async Task<(RoslynExtensionClient? Extension, string? Reason)> ActivateAsync(LspClient client, CancellationToken ct)
    {
        if (!File.Exists(AssemblyPath))
            return (null, $"{AssemblyPath} isn't there");
        IReadOnlyList<string> handlers;
        try
        {
            handlers = await client.ActivateExtensionAsync(AssemblyPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
        string[] needed = [ExtensionMessages.Projects, ExtensionMessages.ProjectDiagnostics, ExtensionMessages.ProjectCompilationDiagnostics];
        var missing = needed.Except(handlers).ToList();
        return missing.Count == 0
            ? (new RoslynExtensionClient(client), null)
            : (null, $"the server didn't register {string.Join(", ", missing)} (built empty, or its extension API changed)");
    }

    /// <summary>The solution's projects and their versions, those with a document at one of <paramref name="priorityPaths"/> first.</summary>
    public async Task<IReadOnlyList<ProjectEntry>> ProjectsAsync(IReadOnlyList<string> priorityPaths, CancellationToken ct)
    {
        var json = await _client.DispatchExtensionMessageAsync(ExtensionMessages.Projects,
            JsonSerializer.Serialize(new ProjectsRequest(priorityPaths)), ct);
        return JsonSerializer.Deserialize<ProjectsResponse>(json)?.Projects ?? [];
    }

    /// <summary>The project's files' compiler diagnostics (quick); null if the project is gone.</summary>
    public Task<RoslynProjectDiagnostics?> DocumentDiagnosticsAsync(string projectId, CancellationToken ct)
        => DiagnosticsAsync(ExtensionMessages.ProjectDiagnostics, projectId, ct);

    /// <summary>What only compiling the whole project finds (unused fields, no Main); null if the project is gone.</summary>
    public Task<RoslynProjectDiagnostics?> CompilationDiagnosticsAsync(string projectId, CancellationToken ct)
        => DiagnosticsAsync(ExtensionMessages.ProjectCompilationDiagnostics, projectId, ct);

    private async Task<RoslynProjectDiagnostics?> DiagnosticsAsync(string message, string projectId, CancellationToken ct)
        => ParseDiagnostics(await _client.DispatchExtensionMessageAsync(message,
            JsonSerializer.Serialize(new ProjectDiagnosticsRequest(projectId)), ct));

    /// <summary>A <see cref="ProjectDiagnosticsResponse"/>, its diagnostics read as the server's own are (they're in the LSP's shape).</summary>
    internal static RoslynProjectDiagnostics? ParseDiagnostics(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True)
            return null;

        var files = new Dictionary<string, IReadOnlyList<LspDiagnostic>>();
        if (root.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var file in list.EnumerateArray())
            {
                if (!file.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } filePath
                    || !file.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
                    continue;
                files[filePath] = [.. diagnostics.EnumerateArray().Select(LspClient.ParseDiagnostic)];
            }

        return new RoslynProjectDiagnostics(
            root.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "",
            files,
            root.TryGetProperty("checked", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0,
            root.TryGetProperty("milliseconds", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt64() : 0);
    }
}
