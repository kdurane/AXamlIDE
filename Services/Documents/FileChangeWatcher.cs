using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Documents;

/// <summary>What happened to a watched file on disk.</summary>
public enum FileChange
{
    Changed,
    Deleted
}

/// <summary>
/// Tells subscribers when a file is changed or deleted on disk by something other than us (git, another editor,
/// a formatter...), so an open tab can reload or warn. Subscribe with <see cref="Watch"/> and dispose the result
/// when done; callbacks run on the UI thread.
///
/// One non-recursive watcher per folder that holds a watched file (not one for the whole workspace, which on Linux
/// costs an inotify watch for every bin/obj/.git folder too). Events are debounced, and a file is only reported
/// when its size or write time really differs from what we last knew, so the several events one save fires
/// become one, and our own saves (DocumentManager.Saved) aren't reported at all.
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<string, Watched> _files = new(GitStatus.PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _folders = new(GitStatus.PathComparer);
    private readonly HashSet<string> _pending = new(GitStatus.PathComparer);
    private readonly DispatcherTimer _timer;

    public FileChangeWatcher(DocumentManager documents)
    {
        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            CheckPending();
        };

        // What we just wrote is the new "known" state, so the events our own save fires are ignored.
        documents.Saved += document => Accept(document.Path);
    }

    /// <summary>
    /// Report changes to <paramref name="path"/> until the result is disposed. Call on the UI thread.
    /// </summary>
    public IDisposable Watch(string path, Action<FileChange> onChange)
    {
        path = Path.GetFullPath(path);
        if (!_files.TryGetValue(path, out var watched))
        {
            watched = new Watched(Stamp.Read(path));
            _files[path] = watched;
            WatchFolder(Path.GetDirectoryName(path)!);
        }

        watched.Subscribers.Add(onChange);
        return new Subscription(this, path, onChange);
    }

    /// <summary>
    /// Re-check every watched file now: for when the main window is activated, in case the OS dropped events
    /// (VS does the same).
    /// </summary>
    public void CheckAll()
    {
        foreach (var path in _files.Keys.ToList())
            Check(path);
    }

    /// <summary>The file on disk is what we expect (we just wrote it): don't report it.</summary>
    public void Accept(string path)
    {
        if (_files.TryGetValue(Path.GetFullPath(path), out var watched))
            watched.Known = Stamp.Read(path);
    }

    private void Unwatch(string path, Action<FileChange> onChange)
    {
        if (!_files.TryGetValue(path, out var watched))
            return;

        watched.Subscribers.Remove(onChange);
        if (watched.Subscribers.Count > 0)
            return;

        _files.Remove(path);
        _pending.Remove(path);
        var folder = Path.GetDirectoryName(path)!;
        if (!_files.Keys.Any(other => GitStatus.PathComparer.Equals(Path.GetDirectoryName(other), folder))
            && _folders.Remove(folder, out var watcher))
            watcher.Dispose();
    }

    // ------------------------------------------------------------- the folder watchers

    private void WatchFolder(string folder)
    {
        if (_folders.ContainsKey(folder) || !Directory.Exists(folder))
            return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            // Saves made by writing a temp file and renaming it over the original arrive as Created/Renamed.
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += (sender, e) =>
            {
                OnFileEvent(sender, e);
                Post(e.OldFullPath);
            };
            watcher.Error += (_, _) => Dispatcher.UIThread.Post(() => RecheckFolder(folder));   // missed events
            watcher.EnableRaisingEvents = true;
            _folders[folder] = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // no live updates for this folder; CheckAll on window activation still catches changes
        }
    }

    // Raised on a thread-pool thread.
    private void OnFileEvent(object sender, FileSystemEventArgs e) => Post(e.FullPath);

    private void Post(string path) => Dispatcher.UIThread.Post(() =>
    {
        if (!_files.ContainsKey(path))
            return;   // another file in the same folder
        _pending.Add(path);
        _timer.Stop();
        _timer.Start();
    });

    private void RecheckFolder(string folder)
    {
        foreach (var path in _files.Keys.Where(path => GitStatus.PathComparer.Equals(Path.GetDirectoryName(path), folder)).ToList())
            Check(path);
    }

    private void CheckPending()
    {
        var paths = _pending.ToList();
        _pending.Clear();
        foreach (var path in paths)
            Check(path);
    }

    private void Check(string path)
    {
        if (!_files.TryGetValue(path, out var watched))
            return;

        var now = Stamp.Read(path);
        if (now == watched.Known)
            return;   // our own save, or several events for one change already reported
        watched.Known = now;

        var change = now.Exists ? FileChange.Changed : FileChange.Deleted;
        foreach (var subscriber in watched.Subscribers.ToList())   // a subscriber may unsubscribe while handling it
            subscriber(change);
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var watcher in _folders.Values)
            watcher.Dispose();
        _folders.Clear();
        _files.Clear();
        _pending.Clear();
    }

    // ------------------------------------------------------------- state

    private sealed class Watched(Stamp known)
    {
        public Stamp Known { get; set; } = known;
        public List<Action<FileChange>> Subscribers { get; } = [];
    }

    /// <summary>What we know about a file without reading it: whether it's there, its write time and its size.</summary>
    private readonly record struct Stamp(bool Exists, DateTime LastWriteUtc, long Length)
    {
        public static Stamp Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new Stamp(true, info.LastWriteTimeUtc, info.Length) : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;   // treated as gone; the next event or CheckAll looks again
            }
        }
    }

    private sealed class Subscription(FileChangeWatcher owner, string path, Action<FileChange> onChange) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.Unwatch(path, onChange);
        }
    }
}
