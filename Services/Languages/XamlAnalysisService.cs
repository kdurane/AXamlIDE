using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// The IDE's own diagnostics for open AXAML files, standing in for a XAML language server: no server handles AXAML,
/// so these are the document's only diagnostics. Two kinds:
///   - xmlns problems (<see cref="XamlNamespaceAnalysis"/>): from the text alone, so always there;
///   - uses of [Obsolete] types and properties (<see cref="XamlObsoleteAnalysis"/>): need the project's metadata,
///     read from its built DLL, so they appear once the project has been built (and are rechecked after each build);
///   - the previewer's error (<see cref="PreviewErrorDiagnostic"/>): what Avalonia's own loader couldn't load, such
///     as a tag missing its "/&gt;". Problems lists it already, as the preview's row; this puts the squiggle there.
/// Worked out when a file opens and again once typing pauses; they go out as <see cref="DiagnosticsChangedMessage"/>
/// like a server's.
/// </summary>
public sealed class XamlAnalysisService
{
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);

    // The first time a project's metadata is read takes seconds: show the xmlns results rather than wait that long.
    private static readonly TimeSpan MetadataWait = TimeSpan.FromMilliseconds(150);

    private readonly XamlLanguageService _xaml;
    private readonly IMessenger _messenger;
    private readonly Dictionary<OpenDocument, Watch> _watched = [];

    private sealed class Watch(DispatcherTimer timer, EventHandler onTextChanged)
    {
        public DispatcherTimer Timer { get; } = timer;
        public EventHandler OnTextChanged { get; } = onTextChanged;
        /// <summary>Bumped on every edit, so a result for older text is dropped.</summary>
        public int Version { get; set; }

        /// <summary>The last analysis, and the <see cref="Version"/> it was for (-1 before the first).</summary>
        public IReadOnlyList<LspDiagnostic> Results { get; set; } = [];
        public int ResultsVersion { get; set; } = -1;

        /// <summary>The preview's current error, or null while it loads fine.</summary>
        public PreviewError? PreviewError { get; set; }
    }

    public XamlAnalysisService(DocumentManager documents, XamlLanguageService xaml, IMessenger messenger)
    {
        _xaml = xaml;
        _messenger = messenger;
        documents.Opened += Start;
        documents.Closing += Stop;
        foreach (var document in documents.Documents)
            Start(document);

        // A build can change what's obsolete (an upgraded package, the project's own types).
        messenger.Register<XamlAnalysisService, BuildFinishedMessage>(this, (service, message) =>
        {
            foreach (var document in service._watched.Keys.ToList())
                _ = service.AnalyseAsync(document);
        });

        messenger.Register<XamlAnalysisService, PreviewProblemMessage>(this, (service, message) =>
            service.OnPreviewProblem(message.Document, message.Error));
    }

    private void Start(OpenDocument document)
    {
        if (document.LanguageId != LanguageIds.Axaml || _watched.ContainsKey(document))
            return;

        var timer = new DispatcherTimer { Interval = TypingPause };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = AnalyseAsync(document);
        };
        Watch? watch = null;
        EventHandler onTextChanged = (_, _) =>
        {
            watch!.Version++;
            timer.Stop();
            timer.Start();
        };
        watch = new Watch(timer, onTextChanged);
        document.Document.TextChanged += onTextChanged;
        _watched[document] = watch;

        _ = AnalyseAsync(document);
    }

    private void Stop(OpenDocument document)
    {
        if (!_watched.Remove(document, out var watch))
            return;
        watch.Timer.Stop();
        document.Document.TextChanged -= watch.OnTextChanged;
    }

    private async Task AnalyseAsync(OpenDocument document)
    {
        if (!_watched.TryGetValue(document, out var watch))
            return;
        var version = watch.Version;
        var text = document.Document.Text;
        var namespaces = XamlNamespaceAnalysis.Find(text);

        var obsolete = ObsoleteUsesAsync(document.Path, text);
        if (await Task.WhenAny(obsolete, Task.Delay(MetadataWait)) != obsolete)
            Apply(document, watch, version, namespaces);

        Apply(document, watch, version, [.. namespaces, .. await obsolete]);
    }

    private async Task<IReadOnlyList<LspDiagnostic>> ObsoleteUsesAsync(string path, string text)
    {
        try
        {
            return await _xaml.GetObsoleteUsesAsync(path, text);
        }
        catch (Exception ex)
        {
            // Not built yet, or not in a project: completion says so when asked; here there's just nothing to flag.
            Debug.WriteLine($"[xaml analysis] No obsolete check for '{path}': {ex.Message}");
            return [];
        }
    }

    private void Apply(OpenDocument document, Watch watch, int version, IReadOnlyList<LspDiagnostic> results)
    {
        // Closed, or edited since: the next pause analyses the new text.
        if (!_watched.TryGetValue(document, out var current) || current != watch || watch.Version != version)
            return;
        watch.Results = results;
        watch.ResultsVersion = version;
        Publish(document, watch);
    }

    private void OnPreviewProblem(OpenDocument document, PreviewError? error)
    {
        if (!_watched.TryGetValue(document, out var watch) || watch.PreviewError == error)
            return;   // not ours, or the same error again (the preview reloads on every edit)
        watch.PreviewError = error;

        // Mid-edit, the analysis due after the pause shows it with the rest; publishing now would put the last
        // analysis's squiggles back at positions the edit has since moved.
        if (watch.ResultsVersion == watch.Version)
            Publish(document, watch);
    }

    private void Publish(OpenDocument document, Watch watch)
    {
        var diagnostics = watch.PreviewError is { } error && PreviewErrorDiagnostic.From(error, document.Document.Text) is { } squiggle
            ? [.. watch.Results, squiggle]
            : watch.Results;
        document.ApplyDiagnostics(diagnostics);
        _messenger.Send(new DiagnosticsChangedMessage(document));
    }
}
