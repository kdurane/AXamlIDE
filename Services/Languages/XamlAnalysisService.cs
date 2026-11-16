using System;
using System.Collections.Generic;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// The IDE's own diagnostics for open AXAML files (see <see cref="XamlNamespaceAnalysis"/>), standing in for a XAML
/// language server: no server handles AXAML, so these are the document's only diagnostics. They're worked out when
/// a file opens and again once typing pauses, and go out as <see cref="DiagnosticsChangedMessage"/> like a server's.
/// </summary>
public sealed class XamlAnalysisService
{
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);

    private readonly IMessenger _messenger;
    private readonly Dictionary<OpenDocument, (DispatcherTimer Timer, EventHandler OnTextChanged)> _watched = [];

    public XamlAnalysisService(DocumentManager documents, IMessenger messenger)
    {
        _messenger = messenger;
        documents.Opened += Watch;
        documents.Closing += Unwatch;
        foreach (var document in documents.Documents)
            Watch(document);
    }

    private void Watch(OpenDocument document)
    {
        if (document.LanguageId != LanguageIds.Axaml || _watched.ContainsKey(document))
            return;

        var timer = new DispatcherTimer { Interval = TypingPause };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Analyse(document);
        };
        EventHandler onTextChanged = (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        document.Document.TextChanged += onTextChanged;
        _watched[document] = (timer, onTextChanged);

        Analyse(document);
    }

    private void Unwatch(OpenDocument document)
    {
        if (!_watched.Remove(document, out var watch))
            return;
        watch.Timer.Stop();
        document.Document.TextChanged -= watch.OnTextChanged;
    }

    private void Analyse(OpenDocument document)
    {
        document.ApplyDiagnostics(XamlNamespaceAnalysis.Find(document.Document.Text));
        _messenger.Send(new DiagnosticsChangedMessage(document));
    }
}
