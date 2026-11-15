using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// Solution-wide analysis, like Rider's and Visual Studio's "entire solution" background analysis: when it's on
/// (<c>Features.SolutionWideAnalysis</c>), Roslyn analyses every file, not just the open ones, and this keeps the
/// diagnostics of the files that aren't open (<see cref="Files"/>). Open files keep coming from
/// LanguageSessionService's per-document pulls, which see unsaved text; Roslyn leaves them out of its answers here.
///
/// How it asks: Roslyn's background analysis scope is a server option (RoslynSettings answers "fullSolution"), and the
/// results come from workspace/diagnostic. That request is a long poll: the first answer is everything, and each
/// later one (sent with the result ids we have) waits until something changes, then says what did. So there's one
/// request always waiting per identifier, restarted whenever the server asks for a refresh.
///
/// Runs on the UI thread (the awaits come back to it); changes go out as <see cref="SolutionDiagnosticsChangedMessage"/>.
/// </summary>
public sealed class SolutionAnalysisService
{
    /// <summary>After an answer with nothing in it, wait this long before asking again, so a server that answers at
    /// once (instead of holding the request) isn't asked in a tight loop.</summary>
    private static readonly TimeSpan EmptyAnswerDelay = TimeSpan.FromSeconds(1);

    private readonly LspManager _servers;
    private readonly IEditorSettings _settings;
    private readonly IMessenger _messenger;

    // What each identifier last said about each file (by URI): its result id and diagnostics.
    private readonly Dictionary<string, Dictionary<string, (string? ResultId, IReadOnlyList<LspDiagnostic> Diagnostics)>> _reports = [];
    private CancellationTokenSource? _polling;
    private int _generation = -1;
    private bool _enabled;

    public SolutionAnalysisService(LspManager servers, IEditorSettings settings, IMessenger messenger)
    {
        _servers = servers;
        _settings = settings;
        _messenger = messenger;
        _enabled = settings.Features.SolutionWideAnalysis;

        settings.PropertyChanged += OnSettingsChanged;
        servers.DiagnosticsRefreshNeeded += OnRefreshNeeded;
        servers.StatusChanged += OnServersChanged;
    }

    /// <summary>Solution-wide analysis is switched on (the setting; it's switched in SettingsViewModel).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>On, and the server has answered at least once: <see cref="Files"/> means something.</summary>
    public bool HasResults { get; private set; }

    /// <summary>The diagnostics of every file the server reported something for, by full path. Empty when off.</summary>
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
            _ = StartAsync();
        }
        else
        {
            Stop();
            Clear();
        }
    }

    private async Task StartAsync()
    {
        if (await _servers.GetClientAsync(LanguageIds.CSharp) is { } client && _enabled)
            Poll(client);
    }

    /// <summary>The server's projects loaded, or it asked for diagnostics to be pulled again.</summary>
    private void OnRefreshNeeded(LspClient client)
    {
        if (_enabled)
            Poll(client);
    }

    /// <summary>The servers were restarted (a new workspace, or Restart Language Servers): what they said is gone.</summary>
    private void OnServersChanged()
    {
        if (_servers.Generation == _generation)
            return;
        _generation = _servers.Generation;
        Stop();
        Clear();
    }

    /// <summary>(Re)start the waiting requests, one per identifier, keeping the results we already have.</summary>
    private void Poll(LspClient client)
    {
        Stop();
        var polling = _polling = new CancellationTokenSource();
        foreach (var identifier in client.WorkspaceDiagnosticIdentifiers)
            _ = PollAsync(client, identifier, polling.Token);
    }

    private void Stop()
    {
        _polling?.Cancel();
        _polling = null;
    }

    private async Task PollAsync(LspClient client, string identifier, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<LspWorkspaceDiagnosticReport> reports;
            try
            {
                reports = await client.WorkspaceDiagnosticsAsync(identifier, ResultIds(identifier), ct);
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

            if (reports.Count > 0)
            {
                Apply(identifier, reports);
            }
            else
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
        }

        HasResults = true;
        Publish();
    }

    private void Clear()
    {
        _reports.Clear();
        HasResults = false;
        Publish();
    }

    /// <summary>Merge the identifiers' reports into one list per file, and tell everyone.</summary>
    private void Publish()
    {
        var byPath = new Dictionary<string, IReadOnlyList<LspDiagnostic>>(GitStatus.PathComparer);
        foreach (var files in _reports.Values)
            foreach (var (uri, report) in files)
            {
                if (report.Diagnostics.Count == 0 || FileUris.ToPath(uri) is not { } path)
                    continue;   // source-generated files and the like have no path to open
                byPath[path] = byPath.TryGetValue(path, out var earlier) ? [.. earlier, .. report.Diagnostics] : report.Diagnostics;
            }

        Files = byPath;
        _messenger.Send(new SolutionDiagnosticsChangedMessage());
    }
}
