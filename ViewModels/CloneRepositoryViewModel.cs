using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Visual Studio's "Clone a repository" (Views/CloneRepositoryWindow): the repository's URL, and the path to clone
/// into, which follows the URL (location + repository name) until it's edited. Cloning shows git's progress line and
/// can be cancelled. <see cref="Done"/> is raised with the cloned folder.
/// </summary>
public sealed partial class CloneRepositoryViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly string _location;
    private CancellationTokenSource? _cloning;
    private bool _pathEdited;
    private bool _settingPath;

    public CloneRepositoryViewModel(string defaultLocation, IDialogService dialogs)
    {
        _dialogs = dialogs;
        _location = defaultLocation;
        _settingPath = true;   // a starting value, not an edit: the path still follows the URL
        TargetPath = defaultLocation;
        _settingPath = false;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string TargetPath { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial bool IsCloning { get; set; }

    /// <summary>Git's latest progress line while cloning, or why it can't clone.</summary>
    [ObservableProperty] public partial string Status { get; set; } = "";

    [ObservableProperty] public partial bool StatusIsError { get; set; }

    /// <summary>The clone finished: the folder it's in.</summary>
    public event Action<string>? Done;

    partial void OnUrlChanged(string value)
    {
        if (_pathEdited)
            return;
        _settingPath = true;
        TargetPath = GitClone.RepositoryName(value) is { Length: > 0 } name ? Path.Combine(_location, name) : _location;
        _settingPath = false;
    }

    partial void OnTargetPathChanged(string value)
    {
        if (!_settingPath)
            _pathEdited = true;
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (await _dialogs.PickFolderAsync() is not { } folder)
            return;
        var name = GitClone.RepositoryName(Url);
        _settingPath = true;
        TargetPath = name.Length > 0 ? Path.Combine(folder, name) : folder;
        _settingPath = false;
        _pathEdited = name.Length == 0;   // with a name, it still follows the URL from the new folder
    }

    private bool CanClone => !IsCloning && Url.Trim().Length > 0 && TargetPath.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task Clone()
    {
        var target = TargetPath.Trim();
        if (!Path.IsPathRooted(target))
        {
            ShowError("The path must be a full path to a folder.");
            return;
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            ShowError($"'{target}' already exists and isn't empty. Choose another path.");
            return;
        }

        _cloning = new CancellationTokenSource();
        IsCloning = true;
        StatusIsError = false;
        Status = "Cloning...";
        try
        {
            await GitClone.CloneAsync(Url, target, line =>
            {
                if (line.Trim().Length > 0)
                    Dispatcher.UIThread.Post(() => { if (IsCloning) Status = line.Trim(); });
            }, _cloning.Token);
            Done?.Invoke(target);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            DeletePartialClone(target);
        }
        catch (GitException ex)
        {
            ShowError(ex.Message);
            DeletePartialClone(target);
        }
        finally
        {
            IsCloning = false;
            _cloning.Dispose();
            _cloning = null;
        }
    }

    /// <summary>Stop a clone that's running (the window's Cancel while cloning); false when none is.</summary>
    public bool CancelClone()
    {
        if (_cloning is null)
            return false;
        _cloning.Cancel();
        return true;
    }

    private void ShowError(string message)
    {
        StatusIsError = true;
        Status = message;
    }

    /// <summary>A failed or cancelled clone leaves a half-made folder behind; git removes it itself, this makes sure.</summary>
    private static void DeletePartialClone(string target)
    {
        try
        {
            if (Directory.Exists(Path.Combine(target, ".git")))
                Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
