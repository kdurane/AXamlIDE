using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Visual Studio's start window (Views/StartWindow): "Open recent" on the left (pinned, then by when each was last
/// opened, with a search box), and "Get started" on the right: clone a repository, open a solution, open a folder,
/// create a new project, or continue without code. Also behind File > Recent, New Project and Clone Repository.
///
/// It never opens anything itself: it sends <see cref="OpenWorkspaceMessage"/> (MainWindowViewModel closes what's
/// open, asking about unsaved changes, then opens it) after raising <see cref="Finished"/>, so the window showing
/// it can close first. Every workspace opened, from anywhere, goes to the top of the recent list (recent.json).
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    private const int MenuCount = 10;

    private readonly RecentWorkspaces _store;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private IReadOnlyList<RecentWorkspace> _recent;

    public StartViewModel(WorkspaceViewModel workspace, RecentWorkspaces store, IDialogService dialogs, IMessenger messenger)
    {
        _store = store;
        _dialogs = dialogs;
        _messenger = messenger;
        _recent = store.Load();
        workspace.Opened += () =>
        {
            if (workspace.WorkspacePath is { } path)
                Update(RecentWorkspaces.Opened(_recent, path, DateTime.Now));
        };
        Rebuild();
    }

    /// <summary>The recent list as shown: groups ("Pinned", "Today"...) of the entries matching <see cref="Search"/>.</summary>
    public ObservableCollection<RecentGroupViewModel> Groups { get; } = [];

    /// <summary>File > Recent Projects and Solutions: the first few, pinned first.</summary>
    [ObservableProperty] public partial IReadOnlyList<RecentItemViewModel> MenuItems { get; private set; } = [];

    public bool HasRecent => _recent.Count > 0;

    public bool HasMenuItems => MenuItems.Count > 0;

    [ObservableProperty] public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Rebuild();

    /// <summary>The solution or folder opened last (for "On startup, open: Most recent solution").</summary>
    public string? MostRecentPath => _recent.OrderByDescending(item => item.LastOpened).FirstOrDefault(item => item.Exists)?.Path;

    /// <summary>
    /// Where new projects and clones go by default: beside the last thing opened (its parent folder), as most people
    /// keep their projects together; else ~/source/repos, Visual Studio's default.
    /// </summary>
    public string DefaultLocation
    {
        get
        {
            foreach (var item in _recent.OrderByDescending(item => item.LastOpened).Where(item => item.Exists))
            {
                var folder = item.IsSolution ? Path.GetDirectoryName(item.Path) : item.Path;
                if (folder is not null && Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { } parent)
                    return parent;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        }
    }

    /// <summary>Something was chosen (or "Continue without code"): the window showing this closes.</summary>
    public event Action? Finished;

    // ------------------------------------------------------------- get started

    [RelayCommand]
    private async Task CloneRepository()
    {
        if (await CloneAsync() is { } path)
            Choose(path);
    }

    [RelayCommand]
    private async Task OpenSolution()
    {
        if (await _dialogs.PickSolutionAsync() is { } path)
            Choose(path);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        if (await _dialogs.PickFolderAsync() is { } path)
            Choose(path);
    }

    [RelayCommand]
    private async Task NewProject()
    {
        if (await _dialogs.NewProjectAsync(DefaultLocation) is { } solution)
            Choose(solution);
    }

    [RelayCommand]
    private void ContinueWithoutCode() => Finished?.Invoke();

    /// <summary>Clone a repository; what to open afterwards (its solution, or the folder), or null if cancelled.</summary>
    private async Task<string?> CloneAsync()
        => await _dialogs.CloneRepositoryAsync(DefaultLocation) is { } folder ? GitClone.PathToOpen(folder) : null;

    // ------------------------------------------------------------- the recent list

    [RelayCommand]
    private async Task OpenRecent(RecentItemViewModel? item)
    {
        if (item is null)
            return;
        if (!item.Item.Exists)
        {
            if (await _dialogs.ConfirmAsync("Open Recent",
                    $"'{item.Item.Path}' can't be found. It may have been moved, renamed or deleted.\n\nRemove it from the list?", "Remove"))
                Update(RecentWorkspaces.Removed(_recent, item.Item.Path));
            return;
        }
        Choose(item.Item.Path);
    }

    [RelayCommand]
    private void RemoveRecent(RecentItemViewModel? item)
    {
        if (item is not null)
            Update(RecentWorkspaces.Removed(_recent, item.Item.Path));
    }

    [RelayCommand]
    private void TogglePin(RecentItemViewModel? item)
    {
        if (item is not null)
            Update(RecentWorkspaces.PinToggled(_recent, item.Item.Path));
    }

    private void Choose(string path)
    {
        Finished?.Invoke();
        _messenger.Send(new OpenWorkspaceMessage(path));
    }

    private void Update(IReadOnlyList<RecentWorkspace> recent)
    {
        _recent = recent;
        _store.Save(recent);
        Rebuild();
    }

    private void Rebuild()
    {
        var search = Search.Trim();
        var matching = search.Length == 0
            ? _recent
            : [.. _recent.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                        || item.Path.Contains(search, StringComparison.OrdinalIgnoreCase))];
        var now = DateTime.Now;
        Groups.Clear();
        foreach (var group in RecentWorkspaces.Group(matching, now))
            Groups.Add(new RecentGroupViewModel(group.Title, [.. group.Items.Select(item => new RecentItemViewModel(item, now))]));

        MenuItems =
        [
            .. _recent.OrderByDescending(item => item.Pinned).ThenByDescending(item => item.LastOpened).Take(MenuCount)
                .Select((item, index) => new RecentItemViewModel(item, now, index + 1))
        ];
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasMenuItems));
    }
}

/// <summary>A heading in the recent list and the entries under it.</summary>
public sealed record RecentGroupViewModel(string Title, IReadOnlyList<RecentItemViewModel> Items);

/// <summary>One recent solution or folder, as the start window and File > Recent show it.</summary>
public sealed class RecentItemViewModel(RecentWorkspace item, DateTime now, int number = 0)
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public RecentWorkspace Item { get; } = item;

    public string Name => Item.Name;

    /// <summary>The folder it's in, with the home folder as ~ (as VS Code does), dimmed under the name.</summary>
    public string Location => Tidy(Path.GetDirectoryName(Item.Path.TrimEnd(Path.DirectorySeparatorChar)) ?? Item.Path);

    public bool IsSolution => Item.IsSolution;

    public bool IsMissing => !Item.Exists;

    public bool IsPinned => Item.Pinned;

    public string PinToolTip => IsPinned ? "Unpin" : "Pin to the top of the list";

    /// <summary>When it was last opened: the time today, else the date.</summary>
    public string When => Item.LastOpened.Date == now.Date
        ? Item.LastOpened.ToString("HH:mm")
        : Item.LastOpened.ToString("dd/MM/yyyy HH:mm");

    public string ToolTip => IsMissing ? $"{Item.Path} (not found)" : Item.Path;

    /// <summary>"_1 ~/Projects/App/App.sln" for File > Recent (underscores in the path doubled so they aren't access keys).</summary>
    public string MenuHeader => $"_{(number == 10 ? 0 : number)} {Tidy(Item.Path).Replace("_", "__")}";

    private static string Tidy(string path)
        => !OperatingSystem.IsWindows() && path.StartsWith(Home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + path[Home.Length..]
            : path;
}
