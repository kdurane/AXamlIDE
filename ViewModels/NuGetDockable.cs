using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.NuGet;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>Which list the NuGet manager shows (Visual Studio's three tabs).</summary>
public enum NuGetTab { Browse, Installed, Updates }

/// <summary>A choice in the package source box: one source, or "All".</summary>
public sealed record NuGetSourceChoice(string Name, IReadOnlyList<NuGetSource> Sources)
{
    public override string ToString() => Name;
}

/// <summary>
/// The NuGet package manager for one project, as a document tab (Views/NuGetView), like Visual Studio's:
///
///   [Browse] [Installed (4)] [Updates (1)]   search...   [ ] Include prerelease   Source: [nuget.org v]
///   package list                              | details: versions, Install / Update / Uninstall, description...
///
/// Sources and the global packages folder are the user's own (dotnet's NuGet configuration); changes go through
/// <c>dotnet add/remove package</c>. The details half is NuGetDockable.Details.cs. Opened by NuGetManager.
/// </summary>
public sealed partial class NuGetDockable : PageDockable
{
    private const int PageSize = 50;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private readonly NuGetService _nuget;
    private readonly IMessenger _messenger;
    private readonly Dictionary<string, InstalledPackage> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _latest = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _listCts = new();
    private readonly CancellationTokenSource _lifetime = new();

    public NuGetDockable(string projectPath, NuGetService nuget, IMessenger messenger)
    {
        _nuget = nuget;
        _messenger = messenger;
        ProjectPath = projectPath;
        ProjectName = Path.GetFileNameWithoutExtension(projectPath);
        Id = IdFor(projectPath);
        Title = $"NuGet: {ProjectName}";
        CanPin = false;
        _ = InitializeAsync();
    }

    public static string IdFor(string projectPath) => "nuget:" + projectPath;

    public string ProjectPath { get; }
    public string ProjectName { get; }
    private string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    public ObservableCollection<NuGetSourceChoice> SourceChoices { get; } = [];
    public ObservableCollection<NuGetPackageRow> Packages { get; } = [];

    [ObservableProperty] public partial NuGetTab Tab { get; set; } = NuGetTab.Browse;
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IncludePrerelease { get; set; }
    [ObservableProperty] public partial NuGetSourceChoice? SelectedSource { get; set; }
    [ObservableProperty] public partial NuGetPackageRow? SelectedPackage { get; set; }

    /// <summary>The list is loading (a progress bar shows).</summary>
    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>Shown instead of the list: nothing found, offline, not restored...</summary>
    [ObservableProperty] public partial string? ListMessage { get; set; }

    [ObservableProperty] public partial int InstalledCount { get; set; }
    [ObservableProperty] public partial int UpdatesCount { get; set; }

    public string InstalledHeader => InstalledCount > 0 ? $"Installed ({InstalledCount})" : "Installed";
    public string UpdatesHeader => UpdatesCount > 0 ? $"Updates ({UpdatesCount})" : "Updates";

    /// <summary><see cref="Tab"/> as the view's tab strip index (Browse, Installed, Updates).</summary>
    public int TabIndex
    {
        get => (int)Tab;
        set
        {
            // The strip reports -1 (nothing selected) while it rebuilds; answering that would loop.
            if (Enum.IsDefined((NuGetTab)value))
                Tab = (NuGetTab)value;
        }
    }

    public bool IsUpdatesTab => Tab == NuGetTab.Updates;

    private IReadOnlyList<NuGetSource> Sources => SelectedSource?.Sources ?? [];

    partial void OnInstalledCountChanged(int value) => OnPropertyChanged(nameof(InstalledHeader));
    partial void OnUpdatesCountChanged(int value)
    {
        OnPropertyChanged(nameof(UpdatesHeader));
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnTabChanged(NuGetTab value)
    {
        OnPropertyChanged(nameof(TabIndex));
        OnPropertyChanged(nameof(IsUpdatesTab));
        _ = ReloadListAsync(delay: false);
    }

    partial void OnSearchTextChanged(string value) => _ = ReloadListAsync(delay: true);
    partial void OnSelectedSourceChanged(NuGetSourceChoice? value) => _ = RefreshAsync();
    partial void OnIncludePrereleaseChanged(bool value) => _ = RefreshAsync();

    // ---------------------------------------------------------------- loading

    private async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            var sources = await _nuget.GetSourcesAsync(ProjectDirectory, _lifetime.Token);
            if (sources.Count > 1)
                SourceChoices.Add(new NuGetSourceChoice("All", sources));
            foreach (var source in sources)
                SourceChoices.Add(new NuGetSourceChoice(source.Name, [source]));
            if (SourceChoices.Count == 0)
            {
                ListMessage = "No package sources are configured (dotnet nuget list source).";
                return;
            }
            SelectedSource = SourceChoices[0];   // loads everything
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ListMessage = "Couldn't read the NuGet configuration: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Re-read what the project has installed and what's newest, then the list.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshInstalledAsync();
        await ReloadListAsync(delay: false);
    }

    private async Task RefreshInstalledAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            var installed = await _nuget.GetInstalledAsync(ProjectPath, ct);
            _installed.Clear();
            foreach (var package in installed.Packages)
                _installed[package.Id] = package;
            InstalledCount = _installed.Count;
            if (installed.Problem is { } problem)
                _messenger.Send(new Messages.StatusMessage($"NuGet: {problem}"));

            var latest = await Task.WhenAll(_installed.Values.Select(async package =>
            {
                try
                {
                    var prerelease = IncludePrerelease || NuGetVersion.TryParse(package.ResolvedVersion) is { IsPrerelease: true };
                    return (package.Id, Version: (await _nuget.GetLatestAsync(Sources, package.Id, prerelease, ct))?.Original);
                }
                catch (HttpRequestException)
                {
                    return (package.Id, Version: (string?)null);
                }
            }));
            _latest.Clear();
            foreach (var (id, version) in latest)
                if (version is not null)
                    _latest[id] = version;

            UpdatesCount = _installed.Values.Count(package => HasUpdate(package.Id));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ListMessage = "Couldn't list the project's packages: " + ex.Message;
        }
    }

    private bool HasUpdate(string id)
        => _installed.TryGetValue(id, out var package)
           && NuGetVersion.TryParse(package.ResolvedVersion) is { } installed
           && _latest.TryGetValue(id, out var latestText)
           && NuGetVersion.TryParse(latestText) is { } latest
           && latest > installed;

    /// <summary>Fill the list for the tab (Browse searches the sources; Installed / Updates filter locally).</summary>
    private async Task ReloadListAsync(bool delay)
    {
        _listCts.Cancel();
        _listCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _listCts.Token;
        var keep = SelectedPackage?.Id;

        try
        {
            if (delay)
                await Task.Delay(SearchDelay, ct);   // wait for typing to pause
            IsLoading = true;
            ListMessage = null;

            var rows = Tab == NuGetTab.Browse ? await SearchRowsAsync(ct) : InstalledRows();
            ct.ThrowIfCancellationRequested();

            Packages.Clear();
            foreach (var row in rows)
                Packages.Add(row);
            ListMessage = Packages.Count > 0 ? null : Tab switch
            {
                NuGetTab.Browse => SearchText.Length > 0 ? $"No packages found for \"{SearchText}\"." : "No packages found.",
                NuGetTab.Installed => "No packages installed.",
                _ => "All packages are up to date."
            };
            SelectedPackage = Packages.FirstOrDefault(row => row.Id.Equals(keep, StringComparison.OrdinalIgnoreCase))
                              ?? Packages.FirstOrDefault();

            foreach (var row in rows)
                _ = LoadRowExtrasAsync(row, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException ex)
        {
            Packages.Clear();
            ListMessage = $"Couldn't reach {SelectedSource?.Name}: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private async Task<List<NuGetPackageRow>> SearchRowsAsync(CancellationToken ct)
    {
        var results = await _nuget.SearchAsync(Sources, SearchText.Trim(), IncludePrerelease, 0, PageSize, ct);
        return [.. results.Select(result =>
        {
            var row = NuGetPackageRow.From(result);
            row.InstalledVersion = _installed.GetValueOrDefault(result.Id)?.ResolvedVersion;
            if (_latest.TryGetValue(result.Id, out var latest))
                row.LatestVersion = latest;
            return row;
        })];
    }

    private List<NuGetPackageRow> InstalledRows()
        => [.. _installed.Values
            .Where(package => Tab != NuGetTab.Updates || HasUpdate(package.Id))
            .Where(package => SearchText.Length == 0 || package.Id.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(package => new NuGetPackageRow(package.Id)
            {
                InstalledVersion = package.ResolvedVersion ?? package.RequestedVersion,
                LatestVersion = _latest.GetValueOrDefault(package.Id)
            })];

    /// <summary>The row's icon, and (for installed rows) its description and authors from the local package.</summary>
    private async Task LoadRowExtrasAsync(NuGetPackageRow row, CancellationToken ct)
    {
        try
        {
            string? localIcon = null;
            if (row.InstalledVersion is { } installed)
            {
                var details = await _nuget.GetLocalDetailsAsync(ProjectDirectory, row.Id, installed, ct);
                row.Description ??= details?.Description;
                row.Authors ??= details?.Authors;
                row.IconUrl ??= details?.IconUrl;
                localIcon = details?.LocalIconPath;
            }
            row.IconData = await _nuget.GetIconAsync(localIcon, row.IconUrl);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------------------------------------------------------------- lifetime

    public override void Dispose()
    {
        _lifetime.Cancel();
        _listCts.Cancel();
        _detailsCts.Cancel();
    }
}
