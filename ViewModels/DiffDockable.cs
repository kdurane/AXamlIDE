using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// A document tab showing one changed file side by side (Views/DiffView), as git sees it:
///
///   unstaged changes   staged version (or HEAD)  vs  the file on disk
///   staged changes     HEAD                      vs  the staged version
///
/// It re-reads both sides whenever the Git status changes, so saving, staging or committing updates it.
/// Opening the same file's diff again (same side) brings this tab back instead of making another.
/// </summary>
public sealed class DiffDockable : PageDockable
{
    private readonly GitService _git;
    private readonly GitStatusTracker _tracker;
    private readonly IDocumentHost _documents;
    private readonly string _repositoryRoot;
    private readonly string _relativePath;
    private readonly string? _originalRelativePath;
    private int _loadVersion;

    public DiffDockable(GitChangeItem item, string repositoryRoot, GitService git, GitStatusTracker tracker, IDocumentHost documents)
    {
        _git = git;
        _tracker = tracker;
        _documents = documents;
        _repositoryRoot = repositoryRoot;
        _relativePath = item.Change.RelativePath;
        _originalRelativePath = item.Change.OriginalRelativePath;
        IsStagedSide = item.IsStagedSide;
        FilePath = item.Change.FullPath;

        Id = IdFor(item);
        Title = $"{item.FileName} ({(IsStagedSide ? "staged" : "changes")})";
        CanPin = false;
        LeftTitle = IsStagedSide ? "HEAD" : "Staged / HEAD";
        RightTitle = IsStagedSide ? "Staged" : "Working tree";

        OpenFileCommand = new RelayCommand(OpenFile, () => File.Exists(FilePath));
        tracker.Changed += OnStatusChanged;
        _ = ReloadAsync();
    }

    /// <summary>The tab id for an item's diff (one tab per file and side).</summary>
    public static string IdFor(GitChangeItem item) => $"diff:{(item.IsStagedSide ? "staged" : "changes")}:{item.Change.FullPath}";

    public string FilePath { get; }
    public bool IsStagedSide { get; }
    public string LeftTitle { get; }
    public string RightTitle { get; }

    /// <summary>The two sides, lined up; null until loaded.</summary>
    public SideBySideDiff? Diff
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(Summary));
        }
    }

    public string Summary => Diff switch
    {
        null => "Loading...",
        { IsBinary: true } => "Binary file",
        { ChangeCount: 0 } => "No changes",
        { ChangeCount: 1 } => "1 change",
        var diff => $"{diff.ChangeCount} changes"
    };

    public RelayCommand OpenFileCommand { get; }

    private void OpenFile()
    {
        try
        {
            _documents.OpenAndActivate(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // gone or unreadable since the button was enabled; the next reload disables it
        }
    }

    private async void OnStatusChanged() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        string left, right;
        if (IsStagedSide)
        {
            left = await _git.ReadCommittedAsync(_repositoryRoot, _originalRelativePath ?? _relativePath) ?? "";
            right = await _git.ReadStagedAsync(_repositoryRoot, _relativePath) ?? "";
        }
        else
        {
            left = await _git.ReadStagedAsync(_repositoryRoot, _relativePath)
                   ?? await _git.ReadCommittedAsync(_repositoryRoot, _relativePath)
                   ?? "";
            right = await ReadDiskAsync();
        }

        var diff = await Task.Run(() => SideBySideDiff.Compare(left, right));
        if (version != _loadVersion)
            return;   // a newer reload started meanwhile

        Diff = diff;
        OpenFileCommand.NotifyCanExecuteChanged();
    }

    private async Task<string> ReadDiskAsync()
    {
        try
        {
            return File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath) : "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The first line of the next (or previous) block of changes after <paramref name="line"/>, or null.</summary>
    public int? NextChange(int line, bool forward)
    {
        if (Diff is not { } diff)
            return null;
        var starts = diff.ChangeStarts().ToList();
        return forward
            ? starts.Cast<int?>().FirstOrDefault(start => start > line) ?? starts.Cast<int?>().FirstOrDefault()
            : starts.Cast<int?>().LastOrDefault(start => start < line) ?? starts.Cast<int?>().LastOrDefault();
    }

    public override void Dispose() => _tracker.Changed -= OnStatusChanged;
}
