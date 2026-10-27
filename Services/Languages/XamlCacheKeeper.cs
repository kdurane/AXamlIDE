using AXamlIDE.Messages;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Languages;

/// <summary>
/// Tells the XAML language service when what it remembers about a project goes out of date, so completion can trust
/// its caches between changes instead of walking the project's folders on every keystroke to check them:
///   - a build finished: the project's DLL may have been rebuilt somewhere else (another configuration);
///   - an .axaml file was saved here, or changed on disk (heard from Git's watcher, which already watches the whole
///     repository): its x:Keys and style classes may have changed.
/// Outside a repository there's no watcher, so a change made by another program is picked up by the completion
/// service's own periodic recheck instead.
/// </summary>
public sealed class XamlCacheKeeper
{
    public XamlCacheKeeper(XamlLanguageService xaml, DocumentManager documents, GitStatusTracker git, IMessenger messenger)
    {
        messenger.Register<XamlCacheKeeper, BuildFinishedMessage>(this, (_, _) => xaml.OnBuildFinished());
        documents.Saved += document => xaml.OnFileChanged(document.Path);
        git.FileChanged += xaml.OnFileChanged;
    }
}
