using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;

namespace AXamlIDE.RoslynExtension;

/// <summary>
/// One project's compiler diagnostics, file by file. Each file's are kept (see <see cref="DiagnosticCache"/>) and
/// worked out again only when its text changes, or when something every file depends on does; an edit inside a method
/// body changes just that file's, so after typing only the edited file is checked again.
/// </summary>
public sealed class ProjectDiagnosticsHandler : IExtensionWorkspaceMessageHandler<ProjectDiagnosticsRequest, ProjectDiagnosticsResponse>
{
    public async Task<ProjectDiagnosticsResponse> ExecuteAsync(ProjectDiagnosticsRequest message,
        ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (DiagnosticCache.FindProject(context.Solution, message.ProjectId) is not { } project)
            return DiagnosticCache.NotFound;

        var stopwatch = Stopwatch.StartNew();
        var version = (await project.GetDependentVersionAsync(cancellationToken)).ToString();
        var (files, checkedFiles) = await DiagnosticCache.DocumentDiagnosticsAsync(project, cancellationToken);
        return new ProjectDiagnosticsResponse(true, version,
            [.. files.Select(pair => new FileDiagnostics(pair.Key, pair.Value))], checkedFiles, stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>
/// What only compiling the whole project finds: the diagnostics the files' own checks can't see (a field nobody
/// uses, a missing Main), less everything <see cref="ProjectDiagnosticsHandler"/> already reports. Kept until anything
/// in the project changes, since any edit can make a field used.
/// </summary>
public sealed class ProjectCompilationDiagnosticsHandler : IExtensionWorkspaceMessageHandler<ProjectDiagnosticsRequest, ProjectDiagnosticsResponse>
{
    public async Task<ProjectDiagnosticsResponse> ExecuteAsync(ProjectDiagnosticsRequest message,
        ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (DiagnosticCache.FindProject(context.Solution, message.ProjectId) is not { } project)
            return DiagnosticCache.NotFound;

        var stopwatch = Stopwatch.StartNew();
        var version = (await project.GetDependentVersionAsync(cancellationToken)).ToString();
        var files = await DiagnosticCache.CompilationOnlyDiagnosticsAsync(project, version, cancellationToken);
        return new ProjectDiagnosticsResponse(true, version,
            [.. files.Select(pair => new FileDiagnostics(pair.Key, pair.Value))], 0, stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>
/// The diagnostics worked out so far, per project, for the handlers above. A file's are good while its text version is
/// the same and so is the project's "key": what every file's diagnostics depend on (the top-level declarations of the
/// project and its references, the generated sources, the .editorconfig files, the options, the references). Lives as
/// long as the server process (the extension is loaded once).
/// </summary>
internal static class DiagnosticCache
{
    internal static readonly ProjectDiagnosticsResponse NotFound = new(false, "", []);

    private static readonly ConcurrentDictionary<ProjectId, ProjectEntryCache> Projects = new();

    // Leave the server some cores for completion and the rest while a project's files are checked.
    private static readonly int Parallelism = Math.Max(1, Environment.ProcessorCount / 2);

    private sealed class ProjectEntryCache(string key)
    {
        public string Key { get; } = key;
        public ConcurrentDictionary<DocumentId, (VersionStamp TextVersion, ImmutableArray<ExtensionDiagnostic> Diagnostics)> Documents { get; } = new();
        public ImmutableArray<ExtensionDiagnostic>? ProjectLevel { get; set; }

        // The compilation-only diagnostics, for the project version they were worked out for.
        public (string Version, Dictionary<string, List<ExtensionDiagnostic>> Files)? CompilationOnly { get; set; }
    }

    internal static Project? FindProject(Solution solution, string projectId)
        => Guid.TryParse(projectId, out var guid) ? solution.GetProject(ProjectId.CreateFromSerialized(guid)) : null;

    /// <summary>Every file's diagnostics (by path; files with none left out), the project-level ones under the project
    /// file, and how many files had to be checked again.</summary>
    internal static async Task<(Dictionary<string, List<ExtensionDiagnostic>> Files, int Checked)> DocumentDiagnosticsAsync(
        Project project, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, List<ExtensionDiagnostic>>(StringComparer.Ordinal);
        if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
            return (files, 0);
        var checkedFiles = 0;

        var cache = await CacheForAsync(project, compilation, cancellationToken);
        var documents = project.Documents.Where(document => document.FilePath is not null && document.SupportsSemanticModel).ToList();

        await Parallel.ForEachAsync(documents,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (document, ct) =>
            {
                var textVersion = await document.GetTextVersionAsync(ct);
                if (cache.Documents.TryGetValue(document.Id, out var known) && known.TextVersion == textVersion)
                    return;
                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null)
                    return;
                var diagnostics = compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: ct)
                    .Where(diagnostic => diagnostic.Location.SourceTree == tree)
                    .Select(Convert).OfType<ExtensionDiagnostic>().ToImmutableArray();
                cache.Documents[document.Id] = (textVersion, diagnostics);
                Interlocked.Increment(ref checkedFiles);
            });

        // Files that left the project.
        var current = documents.Select(document => document.Id).ToHashSet();
        foreach (var gone in cache.Documents.Keys.Where(id => !current.Contains(id)))
            cache.Documents.TryRemove(gone, out _);

        foreach (var document in documents)
            if (cache.Documents.TryGetValue(document.Id, out var entry) && entry.Diagnostics.Length > 0)
                Add(files, document.FilePath!, entry.Diagnostics);

        // Diagnostics with no file: the options' and the references' (the declaration pass reports those).
        cache.ProjectLevel ??= [.. compilation.Options.Errors
            .Concat(compilation.GetDeclarationDiagnostics(cancellationToken).Where(diagnostic => !diagnostic.Location.IsInSource))
            .Select(Convert).OfType<ExtensionDiagnostic>()];
        if (cache.ProjectLevel.Value.Length > 0 && project.FilePath is { } projectFile)
            Add(files, projectFile, cache.ProjectLevel.Value);

        return (files, checkedFiles);
    }

    /// <summary>The whole project compiled, less what <see cref="DocumentDiagnosticsAsync"/> reports.</summary>
    internal static async Task<Dictionary<string, List<ExtensionDiagnostic>>> CompilationOnlyDiagnosticsAsync(
        Project project, string version, CancellationToken cancellationToken)
    {
        if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
            return [];
        var cache = await CacheForAsync(project, compilation, cancellationToken);
        if (cache.CompilationOnly is { } known && known.Version == version)
            return known.Files;

        var reported = (await DocumentDiagnosticsAsync(project, cancellationToken)).Files
            .SelectMany(pair => pair.Value.Select(diagnostic => Identity(pair.Key, diagnostic)))
            .ToHashSet();

        var files = new Dictionary<string, List<ExtensionDiagnostic>>(StringComparer.Ordinal);
        foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
        {
            string? path;
            if (!diagnostic.Location.IsInSource)
                path = project.FilePath;
            else if (diagnostic.Location.SourceTree is { } tree && project.GetDocument(tree) is { FilePath: { } documentPath })
                path = documentPath;
            else
                continue;   // in a generated file: nothing to open
            if (path is null || Convert(diagnostic) is not { } converted || reported.Contains(Identity(path, converted)))
                continue;
            Add(files, path, [converted]);
        }

        cache.CompilationOnly = (version, files);
        return files;
    }

    private static async Task<ProjectEntryCache> CacheForAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(project, compilation, cancellationToken);
        return Projects.AddOrUpdate(project.Id,
            _ => new ProjectEntryCache(key),
            (_, existing) => existing.Key == key ? existing : new ProjectEntryCache(key));
    }

    /// <summary>What every file's diagnostics depend on, besides its own text. See the class summary.</summary>
    private static async Task<string> KeyAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var hash = new HashCode();
        hash.Add((await project.GetDependentSemanticVersionAsync(cancellationToken)).ToString());
        hash.Add(project.CompilationOptions);
        hash.Add(project.ParseOptions);
        foreach (var reference in compilation.References)
            hash.Add(reference.Display);
        foreach (var config in project.AnalyzerConfigDocuments)
            hash.Add((await config.GetTextVersionAsync(cancellationToken)).ToString());
        // A generator's output can change with any edit (a new [ObservableProperty]) and isn't in the versions above.
        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
            hash.Add(Convert64((await generated.GetTextAsync(cancellationToken)).GetContentHash()));
        return hash.ToHashCode().ToString(CultureInfo.InvariantCulture);
    }

    private static string Convert64(ImmutableArray<byte> bytes) => System.Convert.ToBase64String(bytes.AsSpan());

    private static void Add(Dictionary<string, List<ExtensionDiagnostic>> files, string path, IEnumerable<ExtensionDiagnostic> diagnostics)
    {
        if (!files.TryGetValue(path, out var list))
            files[path] = list = [];
        list.AddRange(diagnostics);
    }

    private static (string, string, int, int, int, int, string) Identity(string path, ExtensionDiagnostic diagnostic)
        => (path, diagnostic.Code, diagnostic.Range.Start.Line, diagnostic.Range.Start.Character,
            diagnostic.Range.End.Line, diagnostic.Range.End.Character, diagnostic.Message);

    /// <summary>In the LSP's shape; null for the ones the IDE doesn't list (hidden, or suppressed by #pragma / [SuppressMessage]).</summary>
    private static ExtensionDiagnostic? Convert(Diagnostic diagnostic)
    {
        if (diagnostic.IsSuppressed)
            return null;
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => 1,
            DiagnosticSeverity.Warning => 2,
            DiagnosticSeverity.Info => 3,
            _ => 0
        };
        if (severity == 0)
            return null;

        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        var help = diagnostic.Descriptor.HelpLinkUri;
        return new ExtensionDiagnostic(
            new ExtensionRange(
                new ExtensionPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                new ExtensionPosition(span.EndLinePosition.Line, span.EndLinePosition.Character)),
            severity,
            diagnostic.Id,
            diagnostic.GetMessage(CultureInfo.CurrentUICulture),
            "csharp",
            string.IsNullOrEmpty(help) ? null : new ExtensionCodeDescription(help),
            diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary) ? [1] : null);
    }
}
