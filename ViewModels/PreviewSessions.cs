using System;
using System.Collections.Generic;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Makes each AXAML tab's preview (<see cref="PreviewPaneViewModel"/>, one designer host each) and owns them:
/// a tab's preview is disposed when its document closes, and every one when the app exits (the DI container
/// disposes this), so no designer host outlives the IDE.
/// </summary>
public sealed class PreviewSessions : IDisposable
{
    private readonly XamlLanguageService _xaml;
    private readonly IMessenger _messenger;
    private readonly Dictionary<OpenDocument, PreviewPaneViewModel> _previews = [];

    public PreviewSessions(XamlLanguageService xaml, DocumentManager documents, IMessenger messenger)
    {
        _xaml = xaml;
        messenger.Register<PreviewSessions, BuildStartedMessage>(this, (sessions, _) => sessions.OnBuildStarted());
        messenger.Register<PreviewSessions, BuildFinishedMessage>(this, (sessions, _) => sessions.OnBuildFinished());
        _messenger = messenger;
        documents.Closing += OnDocumentClosing;
    }

    private void OnBuildStarted()
    {
        foreach (var preview in _previews.Values)
            preview.PauseForBuild();
    }

    private void OnBuildFinished()
    {
        foreach (var preview in _previews.Values)
            preview.ResumeAfterBuild();
    }


    /// <summary>The preview of an AXAML document, made (and its host started) the first time it's asked for.</summary>
    public PreviewPaneViewModel For(OpenDocument document)
    {
        if (!_previews.TryGetValue(document, out var preview))
        {
            preview = new PreviewPaneViewModel(document, _xaml, _messenger);
            _previews[document] = preview;
        }
        return preview;
    }

    public void Dispose()
    {
        foreach (var preview in _previews.Values)
            preview.Dispose();
        _previews.Clear();
        _messenger.UnregisterAll(this);
    }

    /// <summary>A tab is closing: its designer host goes with it.</summary>
    private void OnDocumentClosing(OpenDocument document)
    {
        if (_previews.Remove(document, out var preview))
            preview.Dispose();
    }
}
