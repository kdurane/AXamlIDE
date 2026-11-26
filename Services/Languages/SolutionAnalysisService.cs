using AXamlIDE.Services.Diagnostics;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// The diagnostics of the files that aren't open (<see cref="Files"/>), from two sources:
///  - solution-wide analysis (<c>Features.SolutionWideAnalysis</c>), like Rider's: kept live, compiler errors and
///    warnings only (RoslynSettings widens just the compiler's scope), so it stays quick;
///  - Analyze > Run Code Analysis on Solution, like Visual Studio's: one pass with every analyzer (IDE0005, CA1822...)
///    over every file. Its results stay until the next run, the server restarts, or the file is opened (an open
///    file's own pulls take over, with the analyzers, and are live).
/// Open files keep coming from LanguageSessionService's per-document pulls, which see unsaved text; Roslyn leaves
/// them out of its answers here.
///
/// How the live analysis asks: through our extension inside the Roslyn server (<see cref="RoslynExtensionClient"/>),
/// which works on the server's own Solution. Every second (and as soon as typing stops) it asks for the projects'
/// versions; for each project that changed, it asks for the files' compiler diagnostics (only the changed files are
/// checked again, so that's quick) and then for what only compiling the whole project finds (unused fields...). The
/// open files' projects go first. Typing cancels what's being asked; it's asked again once the typing stops.
///
/// If the extension can't be loaded (not built, or a server whose extension API changed), it falls back to Roslyn's
/// own workspace/diagnostic, which is slower: Roslyn's background analysis scope is a server option (RoslynSettings
/// answers "fullSolution"), and that request is a long poll: the first answer is everything, and each later one (sent
/// with the result ids we have) waits until something changes, then goes through the solution again, streaming what
/// it finds. So there's one request always waiting per identifier, restarted whenever the server asks for a refresh.
/// Roslyn has no "run code analysis" request, so a run widens the analyzers' scope too (<see cref="CodeAnalysisRun"/>),
/// asks once with no result ids (everything, at once), and narrows it again. Roslyn holds even that request until a
/// tracked document changes, and would answer it from what it worked out for the narrower scope, so the run first
/// touches a file in every project (<see cref="LspClient.TouchClosedDocument"/>).
///
/// Runs on the UI thread (the awaits come back to it); changes go out as <see cref="SolutionDiagnosticsChangedMessage"/>.
/// </summary>
public sealed class SolutionAnalysisService
{
    /// <summary>After an answer with nothing in it, wait this long before asking again, so a server that answers at
    /// once (instead of holding the request) isn't asked in a tight loop.</summary>
    private static readonly TimeSpan EmptyAnswerDelay = TimeSpan.FromSeconds(1);

    /// <summary>The live analysis asks only once the typing has stopped for this long. Roslyn answers a waiting
    /// request as soon as anything changes, by checking the whole solution again; asking while the user types
    /// would mean a solution-wide check per keystroke, each one out of date before it finished. Short, since the
    /// answer now streams in file by file (partial results) and typing cancels it anyway.</summary>
    private static readonly TimeSpan QuietDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>With the extension, how often to ask whether anything changed when nothing in the IDE said so (a file
    /// changed on disk, by git or another program). Cheap: the server just reports its projects' versions.</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a code analysis run waits for each of Roslyn's steps in taking up the widened options
    /// (reading them, then asking for a refresh, about two seconds later) before going on anyway.</summary>
    private static readonly TimeSpan OptionsTimeout = TimeSpan.FromSeconds(10);

    private readonly LspManager _servers;
    private readonly IEditorSettings _settings;
    private readonly DocumentManager _documents;
    private readonly WorkspaceModel _workspace;
    private readonly CodeAnalysisRun _run;
    private readonly IMessenger _messenger;

    // What each identifier last said about each file (by URI): its result id and diagnostics. (workspace/diagnostic only.)
    private readonly Dictionary<string, Dictionary<string, (string? ResultId, IReadOnlyList<LspDiagnostic> Diagnostics)>> _reports = [];

    // Our extension in the C# server: loading it (once per server), and, once loaded, what it said about each project
    // (by its id). A kick ends the wait between checks: something changed in the IDE.
    private (LspClient Client, Task<(RoslynExtensionClient? Extension, string? Reason)> Loading)? _extension;
    private readonly Dictionary<string, ProjectReport> _projects = [];
    private TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _usingExtension;
    private CancellationTokenSource? _polling;

    // Typing in any open document: when it last happened, and a token that's cancelled when it does (stopping the
    // live requests that are out, which then wait for the typing to stop and ask again).
    private DateTime _lastEdit = DateTime.MinValue;
    private CancellationTokenSource _typing = new();

    // A closed file's compiler diagnostics from when it was open (it was saved, so they're its own), by full path,
    // until Roslyn reports it again; without them its errors from before it was opened would come back meanwhile.
    private readonly Dictionary<string, IReadOnlyList<LspDiagnostic>> _closedSince = new(GitStatus.PathComparer);
    private int _generation = -1;
    private bool _enabled;

    // The last code analysis run's diagnostics, by full path; null before the first run.
    private Dictionary<string, IReadOnlyList<LspDiagnostic>>? _codeAnalysis;
    private CancellationTokenSource? _codeAnalysisRun;

    public SolutionAnalysisService(LspManager servers, IEditorSettings settings, DocumentManager documents,
        WorkspaceModel workspace, CodeAnalysisRun run, IMessenger messenger)
    {
        _workspace = workspace;
        _servers = servers;
        _settings = settings;
        _documents = documents;
        _run = run;
        _messenger = messenger;
        _enabled = settings.Features.SolutionWideAnalysis;

        settings.PropertyChanged += OnSettingsChanged;
        servers.DiagnosticsRefreshNeeded += OnRefreshNeeded;
        servers.StatusChanged += OnServersChanged;
        documents.Opened += OnDocumentOpened;
        documents.Closing += OnDocumentClosing;
        foreach (var document in documents.Documents)
            document.Document.TextChanged += OnTyped;
    }

    /// <summary>Solution-wide analysis is switched on (the setting; it's switched in SettingsViewModel).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>On, and the server has answered at least once: the live part of <see cref="Files"/> means something.</summary>
    public bool HasResults { get; private set; }

    /// <summary>Run Code Analysis on Solution is collecting its pass.</summary>
    public bool IsRunningCodeAnalysis { get; private set; }

    /// <summary>A code analysis run finished and its results are in <see cref="Files"/>.</summary>
    public bool HasCodeAnalysisResults => _codeAnalysis is not null;

    /// <summary>The diagnostics of every file the server reported something for, by full path: the live analysis's
    /// (when on) and the last code analysis run's. Empty when neither has anything.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> Files { get; private set; }
        = new Dictionary<string, IReadOnlyList<LspDiagnostic>>();

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IEditorSettings.Features) || _settings.Features.SolutionWideAnalysis == _enabled)
            return;

        _enabled = _settings.Features.SolutionWideAnalysis;
        if (_enabled)
        {
            // LanguageSessionService tells the server about the new scope; Roslyn then asks for a refresh too.
            Publish();   // "Analysing..." until the first answer
            if (!IsRunningCodeAnalysis)
                _ = StartAsync();
        }
        else
        {
            Stop();
            ClearLive();
        }
    }

    private async Task StartAsync()
    {
        if (await _servers.GetClientAsync(LanguageIds.CSharp) is { } client && _enabled && !IsRunningCodeAnalysis)
            Poll(client);
    }

    /// <summary>The server's projects loaded, or it asked for diagnostics to be pulled again.</summary>
    private void OnRefreshNeeded(LspClient client)
    {
        if (_enabled && !IsRunningCodeAnalysis)
            Poll(client);
    }

    /// <summary>The servers were restarted (a new workspace, or Restart Language Servers): what they said is gone.</summary>
    private void OnServersChanged()
    {
        if (_servers.Generation == _generation)
            return;
        _generation = _servers.Generation;
        _codeAnalysisRun?.Cancel();
        _codeAnalysis = null;
        _extension = null;   // a new server: it needs loading into that one
        Stop();
        ClearLive();
    }

    /// <summary>
    /// An opened file's own pulls take over. What was said about it while it was closed would be stale by the time
    /// it's closed again, so it goes: the run's results, and the live analysis's report and result id (with no result
    /// id, Roslyn reports the file in full once it's closed).
    /// </summary>
    private void OnDocumentOpened(OpenDocument document)
    {
        document.Document.TextChanged += OnTyped;
        _codeAnalysis?.Remove(document.Path);
        _closedSince.Remove(document.Path);
        foreach (var files in _reports.Values)
            files.Remove(document.Uri);
        Publish();
    }

    /// <summary>
    /// A file is closing. With the extension, its diagnostics are already listed (from its open text; closed without
    /// saving, the server goes back to what's on disk, which the next check picks up). Without it, Roslyn doesn't
    /// report open files, so keep a saved file's compiler diagnostics until Roslyn reports it (the next live answer).
    /// </summary>
    private void OnDocumentClosing(OpenDocument document)
    {
        document.Document.TextChanged -= OnTyped;
        Kick();
        if (!_enabled || document.IsDirty || _usingExtension)
            return;   // closed without saving: what it had open isn't what's on disk
        var diagnostics = document.Diagnostics
            .Where(segment => segment.RawDiagnostic is { } raw && raw.ValueKind == JsonValueKind.Object)
            .Select(segment => LspClient.ParseDiagnostic(segment.RawDiagnostic!.Value))
            .Where(IsCompilerDiagnostic)
            .ToList();
        if (diagnostics.Count > 0)
            _closedSince[document.Path] = diagnostics;
        Publish();
    }

    private void OnTyped(object? sender, EventArgs e)
    {
        _lastEdit = DateTime.UtcNow;
        var typing = _typing;
        _typing = new CancellationTokenSource();
        typing.Cancel();
        typing.Dispose();
        Kick();
    }

    /// <summary>Something changed: end the extension's wait between checks (it then waits for the typing to stop).</summary>
    private void Kick() => _kick.TrySetResult();

    /// <summary>Until nothing has been typed for <see cref="QuietDelay"/>.</summary>
    private async Task WaitForQuietAsync(CancellationToken ct)
    {
        while (DateTime.UtcNow - _lastEdit is var since && since < QuietDelay)
            await Task.Delay(QuietDelay - since, ct);
    }

    // ---------------------------------------------------------------- Run Code Analysis on Solution

    /// <summary>
    /// Visual Studio's Analyze > Run Code Analysis > On Solution: every analyzer over every file, once. Returns
    /// straight away if a run is already going. Tells the user how it went in the status bar.
    /// </summary>
    public async Task RunCodeAnalysisAsync()
    {
        if (IsRunningCodeAnalysis)
            return;

        if (await _servers.GetClientAsync(LanguageIds.CSharp) is not { } client || client.WorkspaceDiagnosticIdentifiers.Count == 0)
        {
            Status("Code analysis needs the C# language server, and it isn't running.");
            return;
        }
        if (!_servers.HasLoadedProjects(LanguageIds.CSharp))
        {
            Status("The solution is still loading; run code analysis again when it's ready.");
            return;
        }

        var cancellation = _codeAnalysisRun = new CancellationTokenSource();
        var ct = cancellation.Token;
        IsRunningCodeAnalysis = true;
        Stop();   // the live requests would each do the whole expensive pass too
        Publish();
        Status("Running code analysis on the solution…");

        var found = new Dictionary<string, IReadOnlyList<LspDiagnostic>>(GitStatus.PathComparer);
        var finished = false;
        try
        {
            await WidenAsync(client, ct);
            await TouchEveryProjectAsync(client, ct);
            var answers = await Task.WhenAll(client.WorkspaceDiagnosticIdentifiers.Select(identifier =>
                client.WorkspaceDiagnosticsAsync(identifier, new Dictionary<string, string>(), ct)));
            foreach (var report in answers.SelectMany(reports => reports))
                if (report.Diagnostics.Count > 0 && FileUris.ToPath(report.Uri) is { } path)
                    found[path] = found.TryGetValue(path, out var earlier) ? [.. earlier, .. report.Diagnostics] : report.Diagnostics;
            finished = true;
        }
        catch (OperationCanceledException)
        {
            // the servers restarted; OnServersChanged already cleared everything
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[code analysis] {ex.Message}");
            Status($"Code analysis failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_codeAnalysisRun, cancellation))
                _codeAnalysisRun = null;
            _run.IsRunning = false;
            _servers.SettingsChanged();   // back to the settings' scopes; Roslyn asks for a refresh, which restarts the live requests
            IsRunningCodeAnalysis = false;
        }

        if (finished && !ct.IsCancellationRequested)
        {
            foreach (var document in _documents.Documents)
                found.Remove(document.Path);   // their own pulls have these, live
            _codeAnalysis = found;
            Status(Summary(found));
        }

        if (_enabled && !ct.IsCancellationRequested)
            Poll(client);
        Publish();
    }

    /// <summary>
    /// Tell Roslyn to analyse everything, and wait until it uses that: it reads its options again (workspace/configuration),
    /// applies them a moment later, and then asks for diagnostics to be pulled again (workspace/diagnostic/refresh).
    /// Asking before then gets the narrow scope's answer.
    /// </summary>
    private async Task WidenAsync(LspClient client, CancellationToken ct)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAnswered() => read.TrySetResult();
        void OnRefresh()
        {
            if (read.Task.IsCompleted)
                applied.TrySetResult();   // an earlier refresh (still on its way) isn't about our options
        }

        client.ConfigurationAnswered += OnAnswered;
        client.DiagnosticRefreshRequested += OnRefresh;
        try
        {
            _run.IsRunning = true;
            _servers.SettingsChanged();
            await Task.WhenAny(read.Task, Task.Delay(OptionsTimeout, ct));
            ct.ThrowIfCancellationRequested();
            await Task.WhenAny(applied.Task, Task.Delay(OptionsTimeout, ct));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            client.ConfigurationAnswered -= OnAnswered;
            client.DiagnosticRefreshRequested -= OnRefresh;
        }
    }

    /// <summary>
    /// Open and close (unchanged) one closed C# file in each project, so Roslyn lets the request through and works the
    /// answer out afresh, with every analyzer (see <see cref="LspClient.TouchClosedDocument"/>).
    /// </summary>
    private async Task TouchEveryProjectAsync(LspClient client, CancellationToken ct)
    {
        var open = _documents.Documents.Select(document => document.Path).ToHashSet(GitStatus.PathComparer);
        foreach (var project in _workspace.Projects)
        {
            if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || AClosedSourceFile(project, open) is not { } path)
                continue;
            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            client.TouchClosedDocument(FileUris.FromPath(path), text);
        }
    }

    /// <summary>A .cs file of this project's that isn't open (the first found, outside bin and obj), or null.</summary>
    private string? AClosedSourceFile(string project, HashSet<string> open)
    {
        if (Path.GetDirectoryName(project) is not { } folder || !Directory.Exists(folder))
            return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        try
        {
            return Directory.EnumerateFiles(folder, "*.cs", options).FirstOrDefault(file =>
                !open.Contains(file)
                && !InBuildOutput(Path.GetRelativePath(folder, file))
                && GitStatus.PathComparer.Equals(_workspace.FindProjectForDocument(file), project));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool InBuildOutput(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, 2)[0];
        return first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    private static string Summary(Dictionary<string, IReadOnlyList<LspDiagnostic>> found)
    {
        var all = found.Values.SelectMany(diagnostics => diagnostics).ToList();
        var errors = all.Count(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        var warnings = all.Count(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Warning);
        var messages = all.Count(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Information);
        return $"Code analysis finished: {Count(errors, "error")}, {Count(warnings, "warning")} and {Count(messages, "message")} in files that aren't open.";
    }

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    private void Status(string text) => _messenger.Send(new StatusMessage(text));

    // ---------------------------------------------------------------- the live analysis

    /// <summary>(Re)start the live analysis, keeping the results we already have: through the extension if it loads,
    /// otherwise one waiting workspace/diagnostic request per identifier.</summary>
    private void Poll(LspClient client)
    {
        Stop();
        var polling = _polling = new CancellationTokenSource();
        _ = LiveAsync(client, polling.Token);
    }

    private async Task LiveAsync(LspClient client, CancellationToken ct)
    {
        if (_extension?.Client != client)
            _extension = (client, RoslynExtensionClient.ForAsync(client));
        var loading = _extension.Value.Loading;
        var firstTime = !loading.IsCompleted;

        (RoslynExtensionClient? Extension, string? Reason) loaded;
        try
        {
            loaded = await loading.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            loaded = (null, ex.Message);
        }
        if (ct.IsCancellationRequested)
            return;

        if (loaded.Extension is { } extension)
        {
            if (!_servers.HasLoadedProjects(LanguageIds.CSharp))
                return;   // it would see a half-loaded solution; the projects loading restarts this (OnRefreshNeeded)
            if (!_usingExtension)
                ClearLive();   // what workspace/diagnostic said (Roslyn leaves out the open files; the extension doesn't)
            _usingExtension = true;
            await WatchAsync(client, extension, ct);
            return;
        }

        if (firstTime)
            Debug.WriteLine($"[solution analysis] Roslyn extension not loaded ({loaded.Reason}); using workspace/diagnostic");
        UseWorkspaceDiagnostics(client, ct);
    }

    private void UseWorkspaceDiagnostics(LspClient client, CancellationToken ct)
    {
        if (_usingExtension)
            ClearLive();
        _usingExtension = false;
        foreach (var identifier in client.WorkspaceDiagnosticIdentifiers)
            _ = PollAsync(client, identifier, ct);
    }

    // ---------------------------------------------------------------- the live analysis, through the extension

    /// <summary>What the extension last said about one project: its files' diagnostics and what only compiling the
    /// project found, each with the project version it was for.</summary>
    private sealed class ProjectReport
    {
        public string? FilesVersion;
        public IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> Files = new Dictionary<string, IReadOnlyList<LspDiagnostic>>();
        public string? CompilationVersion;
        public IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> Compilation = new Dictionary<string, IReadOnlyList<LspDiagnostic>>();
    }

    /// <summary>Check, then wait until something changes (or a second goes by), until stopped.</summary>
    private async Task WatchAsync(LspClient client, RoslynExtensionClient extension, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WaitForQuietAsync(ct);
                using (var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _typing.Token))
                    await CheckAsync(extension, request.Token);
                await WaitForChangeAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // typing started: check again once it stops
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The extension failed (its API changed under a server update, say): go back to Roslyn's own way.
                Debug.WriteLine($"[solution analysis] Roslyn extension failed ({ex.Message}); using workspace/diagnostic");
                _extension = (client, Task.FromResult<(RoslynExtensionClient?, string?)>((null, ex.Message)));
                UseWorkspaceDiagnostics(client, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Ask which projects changed; for each, its files' diagnostics (each shown as it comes), then what only compiling
    /// it finds. Projects that are gone are dropped.
    /// </summary>
    private async Task CheckAsync(RoslynExtensionClient extension, CancellationToken ct)
    {
        List<string> open = [.. _documents.Documents.Select(document => document.Path)];
        if (_documents.Active is { } active)
            open.Insert(0, active.Path);
        var projects = await extension.ProjectsAsync(open, ct);

        var current = projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        var gone = _projects.Keys.Where(id => !current.Contains(id)).ToList();
        foreach (var id in gone)
            _projects.Remove(id);
        if (gone.Count > 0)
            Publish();

        foreach (var project in projects)
        {
            if (_projects.TryGetValue(project.Id, out var known) && known.FilesVersion == project.Version)
                continue;
            if (await extension.DocumentDiagnosticsAsync(project.Id, ct) is not { } result)
                continue;   // gone since
            if (!_projects.TryGetValue(project.Id, out var report))
                _projects[project.Id] = report = new ProjectReport();
            report.FilesVersion = result.Version;
            report.Files = result.Files;
            Debug.WriteLine($"[solution analysis] {project.Name}: {result.Checked} files checked in {result.Milliseconds} ms");
            Publish();
        }

        if (!HasResults)
        {
            HasResults = true;
            PerfTimeline.Mark(PerfTimeline.Open, "first-solution-results");
            Publish();
        }

        foreach (var project in projects)
        {
            if (!_projects.TryGetValue(project.Id, out var report) || report.CompilationVersion == report.FilesVersion)
                continue;
            if (await extension.CompilationDiagnosticsAsync(project.Id, ct) is not { } result)
                continue;
            report.CompilationVersion = result.Version;
            report.Compilation = result.Files;
            Debug.WriteLine($"[solution analysis] {project.Name}: compiled in {result.Milliseconds} ms");
            Publish();
        }
    }

    /// <summary>Until something in the IDE changes (<see cref="Kick"/>), or <see cref="WatchInterval"/> goes by.</summary>
    private async Task WaitForChangeAsync(CancellationToken ct)
    {
        var kick = _kick.Task;
        await Task.WhenAny(kick, Task.Delay(WatchInterval, ct));
        ct.ThrowIfCancellationRequested();
        if (kick.IsCompleted)
            _kick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ---------------------------------------------------------------- the live analysis, through workspace/diagnostic

    private void Stop()
    {
        _polling?.Cancel();
        _polling = null;
    }

    /// <summary>
    /// One identifier's waiting request, asked again each time it's answered. Roslyn streams the answer as partial
    /// results, a batch of files at a time as it checks them; each batch goes into the Problems list as it comes
    /// (on the UI thread, in order, before the final answer), so files show up as they're done rather than when the
    /// whole solution is.
    /// </summary>
    private async Task PollAsync(LspClient client, string identifier, CancellationToken ct)
    {
        var ui = SynchronizationContext.Current;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<LspWorkspaceDiagnosticReport> reports;
            var streamed = 0;

            // Batches come on the reading thread, often one file each. Those that arrive while the UI thread is busy
            // are gathered and applied together, so the Problems list is rebuilt once per turn, not once per file.
            var pending = new List<LspWorkspaceDiagnosticReport>();
            void ApplyPending()
            {
                List<LspWorkspaceDiagnosticReport> batch;
                lock (pending)
                {
                    batch = [.. pending];
                    pending.Clear();
                }
                if (batch.Count == 0 || ct.IsCancellationRequested)
                    return;   // stopped (servers restarted, or a code analysis run): these are stale
                streamed += batch.Count;
                Apply(identifier, batch);
            }
            void OnBatch(IReadOnlyList<LspWorkspaceDiagnosticReport> batch)
            {
                bool first;
                lock (pending)
                {
                    first = pending.Count == 0;
                    pending.AddRange(batch);
                }
                if (!first)
                    return;   // already on its way to the UI thread
                if (ui is null)
                    ApplyPending();
                else
                    ui.Post(_ => ApplyPending(), null);
            }

            try
            {
                await WaitForQuietAsync(ct);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _typing.Token);
                reports = await client.WorkspaceDiagnosticsAsync(identifier, ResultIds(identifier), request.Token, OnBatch);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;   // typing started: ask again when it stops (Roslyn has noted the change, so it answers then)
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The server stopped, or doesn't like the request: wait for the next refresh or restart.
                Debug.WriteLine($"[solution analysis] {identifier}: {ex.Message}");
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            ApplyPending();   // normally already applied (posted before this continuation), but be sure before the next ask
            if (reports.Count > 0)
                Apply(identifier, reports);
            if (reports.Count == 0 && streamed == 0)
            {
                try
                {
                    await Task.Delay(EmptyAnswerDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private Dictionary<string, string> ResultIds(string identifier)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_reports.TryGetValue(identifier, out var files))
            foreach (var (uri, report) in files)
                if (report.ResultId is { } resultId)
                    ids[uri] = resultId;
        return ids;
    }

    private void Apply(string identifier, IReadOnlyList<LspWorkspaceDiagnosticReport> reports)
    {
        if (!_reports.TryGetValue(identifier, out var files))
            _reports[identifier] = files = new Dictionary<string, (string?, IReadOnlyList<LspDiagnostic>)>(StringComparer.Ordinal);

        foreach (var report in reports)
        {
            if (report.Unchanged && files.TryGetValue(report.Uri, out var known))
                files[report.Uri] = (report.ResultId ?? known.ResultId, known.Diagnostics);
            else
                files[report.Uri] = (report.ResultId, report.Diagnostics);
            if (FileUris.ToPath(report.Uri) is { } path)
                _closedSince.Remove(path);
        }

        HasResults = true;
        PerfTimeline.Mark(PerfTimeline.Open, "first-solution-results");
        Publish();
    }

    private void ClearLive()
    {
        _reports.Clear();
        _projects.Clear();
        _closedSince.Clear();
        HasResults = false;
        Publish();
    }

    // ---------------------------------------------------------------- merging

    /// <summary>
    /// Merge the identifiers' reports and the last run into one list per file, and tell everyone. Where both have a
    /// file, the live analysis is newer for the compiler's diagnostics; the run is the only source of the analyzers'.
    /// </summary>
    private void Publish()
    {
        var live = new Dictionary<string, IReadOnlyList<LspDiagnostic>>(GitStatus.PathComparer);
        foreach (var files in _reports.Values)
            foreach (var (uri, report) in files)
            {
                if (report.Diagnostics.Count == 0 || FileUris.ToPath(uri) is not { } path)
                    continue;   // source-generated files and the like have no path to open
                live[path] = live.TryGetValue(path, out var earlier) ? [.. earlier, .. report.Diagnostics] : report.Diagnostics;
            }
        foreach (var (path, diagnostics) in ExtensionFiles())
            live[path] = live.TryGetValue(path, out var earlier) ? [.. earlier, .. diagnostics] : diagnostics;
        foreach (var (path, diagnostics) in _closedSince)
            live.TryAdd(path, diagnostics);

        if (_codeAnalysis is null)
        {
            Files = live;
        }
        else
        {
            var merged = new Dictionary<string, IReadOnlyList<LspDiagnostic>>(GitStatus.PathComparer);
            var liveCompiler = HasResults;   // the live analysis has answered, so a file it doesn't list has no compiler errors
            foreach (var (path, diagnostics) in _codeAnalysis)
            {
                IReadOnlyList<LspDiagnostic> compiler = liveCompiler
                    ? live.GetValueOrDefault(path)?.Where(IsCompilerDiagnostic).ToList() ?? []
                    : diagnostics.Where(IsCompilerDiagnostic).ToList();
                List<LspDiagnostic> all = [.. compiler, .. diagnostics.Where(diagnostic => !IsCompilerDiagnostic(diagnostic))];
                if (all.Count > 0)
                    merged[path] = all;
            }
            foreach (var (path, diagnostics) in live)
                merged.TryAdd(path, diagnostics);
            Files = merged;
        }

        _messenger.Send(new SolutionDiagnosticsChangedMessage());
    }

    /// <summary>
    /// The extension's reports merged by file: each project's files' and compilation's. A file in two projects (a
    /// project built for two frameworks) is listed by both, so the same diagnostic is kept once.
    /// </summary>
    private Dictionary<string, IReadOnlyList<LspDiagnostic>> ExtensionFiles()
    {
        var merged = new Dictionary<string, List<LspDiagnostic>>(GitStatus.PathComparer);
        var seen = new HashSet<(string, int, int, int, int, string)>();
        foreach (var report in _projects.Values)
            foreach (var files in (IEnumerable<IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>>>)[report.Files, report.Compilation])
                foreach (var (path, diagnostics) in files)
                    foreach (var diagnostic in diagnostics)
                    {
                        if (!seen.Add((path, diagnostic.StartLine, diagnostic.StartChar, diagnostic.EndLine, diagnostic.EndChar, diagnostic.Message)))
                            continue;
                        if (!merged.TryGetValue(path, out var list))
                            merged[path] = list = [];
                        list.Add(diagnostic);
                    }
        return merged.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LspDiagnostic>)pair.Value, GitStatus.PathComparer);
    }

    /// <summary>The C# compiler's own diagnostics (CS0103...), which the live analysis also reports; the rest are
    /// analyzers' (IDE0005, CA1822, a NuGet package's).</summary>
    private static bool IsCompilerDiagnostic(LspDiagnostic diagnostic)
    {
        var code = LspHelpers.DiagnosticCode(diagnostic.Raw);
        return code.Length > 2 && code.StartsWith("CS", StringComparison.Ordinal) && code.Skip(2).All(char.IsAsciiDigit);
    }
}
