using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Editor.GitChanges;

/// <summary>
/// Which lines of the editor's document differ from git (VS Code's quick diff, VS's and Rider's change markers):
/// the text being edited, unsaved changes included, against the staged text, or the committed text when nothing
/// is staged (the same base as the Git tab's "changes" diff). Drawn by <see cref="GitChangeMargin"/> beside the
/// line numbers and by the overview ruler.
///
/// The base text is read when a document is attached and again when git's status says it may have changed (a
/// commit, stage, checkout); the comparison runs off the UI thread a moment after typing stops. Untracked and
/// ignored files, and files outside the repository, get no marks.
/// </summary>
public sealed class GitChangeController : IDisposable
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);

    private readonly EditorContext _ctx;
    private readonly GitService _git;
    private readonly GitStatusTracker _tracker;
    private readonly DispatcherTimer _timer;
    private string? _baseText;
    private object? _baseKey;     // what the base text was read from; a status with the same key needn't re-read it
    private int _baseVersion;     // bumped by every attach and base read, so a late read is dropped
    private int _compareVersion;  // the same for comparisons

    public GitChangeController(EditorContext ctx, GitService git, GitStatusTracker tracker)
    {
        _ctx = ctx;
        _git = git;
        _tracker = tracker;
        _timer = new DispatcherTimer { Interval = TypingDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = CompareAsync();
        };

        ctx.Attached += OnAttached;
        ctx.TextChanged += _ =>
        {
            if (_baseText is null)
                return;
            _timer.Stop();
            _timer.Start();
        };
        tracker.Changed += OnStatusChanged;
    }

    /// <summary>The changed blocks, in line order; empty when there's nothing to show.</summary>
    public IReadOnlyList<LineChange> Changes { get; private set; } = [];

    /// <summary>Raised on the UI thread when <see cref="Changes"/> is replaced.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        _timer.Stop();
        _ctx.Attached -= OnAttached;
        _tracker.Changed -= OnStatusChanged;
    }

    private void OnAttached(OpenDocument? document)
    {
        _baseText = null;
        _baseKey = null;
        _timer.Stop();
        ++_baseVersion;
        ++_compareVersion;
        Show([]);
        if (document is not null)
            _ = LoadBaseAsync(document);
    }

    private void OnStatusChanged()
    {
        if (_ctx.Active is { } document && !Equals(BaseKey(document), _baseKey))
            _ = LoadBaseAsync(document);
    }

    /// <summary>
    /// What the base text depends on, or null when the file has none (outside the repository, untracked, ignored).
    /// Before the status is known, a key that always reads.
    /// </summary>
    private object? BaseKey(OpenDocument document)
    {
        if (_tracker.Current is not { } status || RelativePath(status.RepositoryRoot, document.Path) is null)
            return null;
        if (status.StateOfFile(document.Path) is GitFileState.Untracked or GitFileState.Ignored)
            return null;
        var change = status.ChangeFor(document.Path);
        return (status.RepositoryRoot, status.HeadCommit, change?.HeadObject, change?.IndexObject, change?.IndexState);
    }

    private async Task LoadBaseAsync(OpenDocument document)
    {
        var version = ++_baseVersion;
        var key = BaseKey(document);
        string? baseText = null;
        if (key is not null && _tracker.Current is { } status && RelativePath(status.RepositoryRoot, document.Path) is { } relative)
        {
            try
            {
                baseText = await _git.ReadStagedAsync(status.RepositoryRoot, relative)
                           ?? await _git.ReadCommittedAsync(status.RepositoryRoot, relative);
            }
            catch (GitException)
            {
                // git went away or the repository is broken: no marks rather than wrong ones
            }
        }

        if (version != _baseVersion || !ReferenceEquals(_ctx.Active, document))
            return;
        _baseKey = key;
        _baseText = baseText?.ReplaceLineEndings("\n");
        if (_baseText is null)
        {
            ++_compareVersion;   // a comparison against the old base mustn't land after this
            Show([]);
        }
        else
            await CompareAsync();
    }

    private async Task CompareAsync()
    {
        if (_baseText is not { } baseText || _ctx.Active is not { } document)
            return;
        var version = ++_compareVersion;
        var text = document.Document.Text;   // read on the UI thread; compared off it
        var changes = await Task.Run(() => LineChanges.Compare(baseText, text.ReplaceLineEndings("\n")));
        if (version == _compareVersion && ReferenceEquals(_ctx.Active, document))
            Show(changes);
    }

    private void Show(IReadOnlyList<LineChange> changes)
    {
        if (changes.Count == 0 && Changes.Count == 0)
            return;
        Changes = changes;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- one change: what it was, putting it back

    /// <summary>The change on a 1-based line (a deletion counts on the line it's below), or null.</summary>
    public LineChange? ChangeAt(int line)
        => Changes.Where(change => change.Contains(line)).Cast<LineChange?>().FirstOrDefault();

    /// <summary>The next change after the line (or the previous one before it), wrapping round, as VS Code's Alt+F5.</summary>
    public LineChange? Neighbour(int line, bool next)
    {
        if (Changes.Count == 0)
            return null;
        var current = ChangeAt(line);
        return next
            ? Changes.Where(change => Line(change) > (current is { } c ? Line(c) : line)).Cast<LineChange?>().FirstOrDefault() ?? Changes[0]
            : Changes.Where(change => Line(change) < (current is { } c2 ? Line(c2) : line)).Cast<LineChange?>().LastOrDefault() ?? Changes[^1];
    }

    /// <summary>The line a change is shown on (a deletion at the top: line 1).</summary>
    public static int Line(LineChange change) => Math.Max(1, change.FirstLine);

    /// <summary>The lines the change replaced, as they are in git (empty for added lines).</summary>
    public IReadOnlyList<string> BaseLines(LineChange change)
        => _baseText is { } baseText ? LineChanges.BaseLines(baseText, change) : [];

    /// <summary>
    /// VS Code's Revert Change, Rider's Rollback Lines: put one block back as it is in git. An ordinary edit, so
    /// Ctrl+Z brings it back. Nothing happens if the change is out of date (typing since).
    /// </summary>
    public bool Revert(LineChange change)
    {
        if (_baseText is null || _ctx.Active is not { } document || !Changes.Contains(change))
            return false;
        var text = document.Document.Text;
        var edit = LineChanges.Revert(text, change, BaseLines(change));
        if (edit.Offset + edit.Length > text.Length)
            return false;
        document.Document.Replace(edit.Offset, edit.Length, edit.Text);
        _ = CompareAsync();
        return true;
    }

    /// <summary>The path relative to the repository, with '/' as git writes it, or null when it's outside.</summary>
    private static string? RelativePath(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? null
            : relative.Replace('\\', '/');
    }
}
