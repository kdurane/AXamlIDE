using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Services.Git;

/// <summary>
/// The Git status of the open workspace's repository, kept current: re-read when a workspace opens, when a
/// document is saved, and when files change on disk (including git's own files, so commits and branch
/// switches made outside the IDE show up). Changes are debounced, so a build or checkout causes one refresh.
/// <see cref="Changed"/> is raised on the UI thread after every refresh.
/// </summary>
public sealed class GitStatusTracker : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly GitService _git;
    private readonly WorkspaceModel _workspace;
    private readonly DispatcherTimer _timer;
    private FileSystemWatcher? _watcher;
    private bool _refreshing;
    private bool _refreshAgain;
    private int _workspaceVersion;

    public GitStatusTracker(GitService git, WorkspaceModel workspace, DocumentManager documents)
    {
        _git = git;
        _workspace = workspace;
        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            await RefreshAsync();
        };

        workspace.WorkspaceRoots.CollectionChanged += OnWorkspaceChanged;
        documents.Saved += _ => RequestRefresh();
    }

    /// <summary>The repository the workspace is in, or null (no workspace, or not in a repository).</summary>
    public string? RepositoryRoot { get; private set; }

    /// <summary>The last status read, or null outside a repository.</summary>
    public GitStatus? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Refresh soon (several requests close together become one refresh).</summary>
    public void RequestRefresh()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Re-read the status now. If a refresh is already running, one more follows it.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var version = _workspaceVersion;
                var root = RepositoryRoot;
                GitStatus? status = null;
                if (root is not null)
                {
                    try
                    {
                        status = await _git.GetStatusAsync(root);
                    }
                    catch (GitException)
                    {
                        // e.g. the repository was deleted; show nothing rather than stale state
                    }
                }

                if (version == _workspaceVersion)   // the workspace didn't change while git ran
                {
                    Current = status;
                    Changed?.Invoke();
                }
            } while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void OnWorkspaceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var version = ++_workspaceVersion;
        StopWatching();
        RepositoryRoot = null;
        Current = null;
        Changed?.Invoke();

        if (_workspace.RootDirectory is not { } directory)
            return;
        var root = await _git.FindRepositoryRootAsync(directory);
        if (version != _workspaceVersion || root is null)
            return;

        RepositoryRoot = root;
        Watch(root);
        await RefreshAsync();
    }

    // ------------------------------------------------------------- watching the disk

    private void Watch(string root)
    {
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += (_, _) => Dispatcher.UIThread.Post(RequestRefresh);   // missed events: just re-read
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            StopWatching();   // no live updates; saves and the Refresh button still work
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (Matters(e.FullPath))
            Dispatcher.UIThread.Post(RequestRefresh);
    }

    /// <summary>
    /// Whether a change to this path can change the status. Inside .git only the index, HEAD and refs do
    /// (not objects, logs or lock files); build output (bin, obj) is skipped so a build doesn't refresh constantly.
    /// </summary>
    private static bool Matters(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        var git = $"{separator}.git{separator}";
        var inGit = path.IndexOf(git, StringComparison.Ordinal);
        if (inGit >= 0)
        {
            var inside = path[(inGit + git.Length)..];
            return !inside.EndsWith(".lock", StringComparison.Ordinal)
                   && (inside is "index" or "HEAD" or "MERGE_HEAD" || inside.StartsWith("refs", StringComparison.Ordinal));
        }

        return !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
               && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        StopWatching();
    }
}
