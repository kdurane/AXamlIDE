using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.NuGet;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The NuGet manager's details pane: the selected package's versions, the chosen version's details, and
/// Install / Update / Downgrade / Uninstall (dotnet add / remove package, then everything is re-read).
/// </summary>
public sealed partial class NuGetDockable
{
    private CancellationTokenSource _detailsCts = new();

    public ObservableCollection<string> Versions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    public partial string? SelectedVersion { get; set; }

    [ObservableProperty] public partial NuGetPackageDetails? Details { get; set; }
    [ObservableProperty] public partial byte[]? DetailsIcon { get; set; }
    [ObservableProperty] public partial bool IsLoadingDetails { get; set; }

    /// <summary>An install / update / uninstall is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand), nameof(UninstallSelectedCommand), nameof(UpdateAllCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What the last change printed when it failed (shown under the buttons).</summary>
    [ObservableProperty] public partial string? ActionError { get; set; }

    /// <summary>The installed version of the selected package, or null.</summary>
    public string? SelectedInstalledVersion => SelectedPackage?.InstalledVersion;

    /// <summary>"Install", "Update", "Downgrade", or "Installed" when the chosen version is the one installed.</summary>
    public string PrimaryActionText
    {
        get
        {
            if (SelectedInstalledVersion is null)
                return "Install";
            var installed = NuGetVersion.TryParse(SelectedInstalledVersion);
            var chosen = NuGetVersion.TryParse(SelectedVersion);
            if (installed is null || chosen is null || chosen.Equals(installed))
                return "Installed";
            return chosen > installed ? "Update" : "Downgrade";
        }
    }

    public string DependenciesText => Details is not { DependencyGroups.Count: > 0 } details
        ? "No dependencies"
        : string.Join(Environment.NewLine + Environment.NewLine, details.DependencyGroups.Select(group =>
            group.TargetFramework + Environment.NewLine + (group.Dependencies.Count == 0
                ? "    No dependencies"
                : string.Join(Environment.NewLine, group.Dependencies.Select(d => $"    {d.Id} ({d.Range ?? "any"})")))));

    public string? LicenseText => Details?.LicenseExpression ?? (Details?.LicenseUrl is null ? null : "View licence");
    public string? LicenseLink => Details?.LicenseExpression is { } expression
        ? $"https://licenses.nuget.org/{Uri.EscapeDataString(expression)}"
        : Details?.LicenseUrl;
    public string? PublishedText => Details?.Published?.ToLocalTime().ToString("d MMMM yyyy");
    public string? TagsText => Details is { Tags.Count: > 0 } d ? string.Join(", ", d.Tags) : null;
    public string? VulnerabilitiesText => Details is { Vulnerabilities.Count: > 0 } d ? string.Join(Environment.NewLine, d.Vulnerabilities) : null;

    partial void OnDetailsChanged(NuGetPackageDetails? value)
    {
        OnPropertyChanged(nameof(DependenciesText));
        OnPropertyChanged(nameof(LicenseText));
        OnPropertyChanged(nameof(LicenseLink));
        OnPropertyChanged(nameof(PublishedText));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(VulnerabilitiesText));
    }

    partial void OnSelectedPackageChanged(NuGetPackageRow? value)
    {
        OnPropertyChanged(nameof(SelectedInstalledVersion));
        OnPropertyChanged(nameof(PrimaryActionText));
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        ActionError = null;
        _ = LoadVersionsAsync(value);
    }

    partial void OnSelectedVersionChanged(string? value) => _ = LoadDetailsAsync(SelectedPackage, value);

    // ---------------------------------------------------------------- loading

    private async Task LoadVersionsAsync(NuGetPackageRow? row)
    {
        _detailsCts.Cancel();
        _detailsCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _detailsCts.Token;

        SelectedVersion = null;   // so the same version string on the next package still loads its details
        Versions.Clear();
        Details = null;
        DetailsIcon = row?.IconData;
        if (row is null)
            return;

        IsLoadingDetails = true;
        try
        {
            var prerelease = IncludePrerelease || NuGetVersion.TryParse(row.InstalledVersion) is { IsPrerelease: true };
            var versions = (await _nuget.GetVersionsAsync(Sources, row.Id, prerelease, ct)).Select(v => v.Original).ToList();
            ct.ThrowIfCancellationRequested();
            if (row.InstalledVersion is { } installed && !versions.Contains(installed, StringComparer.OrdinalIgnoreCase))
                versions.Insert(0, installed);   // from a source we can't browse, or unlisted
            foreach (var version in versions)
                Versions.Add(version);

            // Like Visual Studio: Updates offers the newest; otherwise show what's installed, else the newest.
            SelectedVersion = Tab == NuGetTab.Updates || row.InstalledVersion is null
                ? row.LatestVersion is { } latest && Versions.Contains(latest) ? latest : Versions.FirstOrDefault()
                : row.InstalledVersion;
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException ex)
        {
            ActionError = "Couldn't read the versions: " + ex.Message;
            if (row.InstalledVersion is { } installed)
            {
                Versions.Add(installed);
                SelectedVersion = installed;
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoadingDetails = false;
        }
    }

    private async Task LoadDetailsAsync(NuGetPackageRow? row, string? version)
    {
        if (row is null || version is null)
        {
            Details = null;
            return;
        }

        var ct = _detailsCts.Token;
        try
        {
            var details = await _nuget.GetDetailsAsync(Sources, ProjectDirectory, row.Id, version, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(row, SelectedPackage) || version != SelectedVersion)
                return;
            Details = details;
            DetailsIcon = await _nuget.GetIconAsync(details?.LocalIconPath, details?.IconUrl ?? row.IconUrl) ?? row.IconData;
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException ex)
        {
            ActionError = "Couldn't read the package details: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- changing the project

    /// <summary>Install the chosen version, or change the installed one to it.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedAsync()
    {
        if (SelectedPackage is not { } row || SelectedVersion is not { } version)
            return;
        var verb = PrimaryActionText;
        await ChangeAsync($"{verb} {row.Id} {version}",
            ct => _nuget.InstallAsync(ProjectPath, row.Id, version, ct),
            $"{(verb == "Install" ? "Installed" : verb == "Update" ? "Updated" : "Downgraded")} {row.Id} to {version}.");
    }

    private bool CanInstallSelected() => !IsBusy && SelectedPackage is not null && SelectedVersion is not null && PrimaryActionText != "Installed";

    [RelayCommand(CanExecute = nameof(CanUninstallSelected))]
    private async Task UninstallSelectedAsync()
    {
        if (SelectedPackage is not { } row)
            return;
        await ChangeAsync($"Uninstall {row.Id}", ct => _nuget.UninstallAsync(ProjectPath, row.Id, ct), $"Uninstalled {row.Id}.");
    }

    private bool CanUninstallSelected() => !IsBusy && SelectedPackage is { IsInstalled: true };

    /// <summary>The Updates tab's "Update All": every package with a newer version, one after another.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        var updates = _installed.Keys.Where(HasUpdate).Select(id => (Id: id, Version: _latest[id])).ToList();
        await ChangeAsync($"Update {updates.Count} package{(updates.Count == 1 ? "" : "s")}", async ct =>
        {
            foreach (var (id, version) in updates)
            {
                var result = await _nuget.InstallAsync(ProjectPath, id, version, ct);
                if (!result.Succeeded)
                    return result with { Output = $"{id} {version}:{Environment.NewLine}{result.Output}" };
            }
            return new NuGetChangeResult(true, "");
        }, $"Updated {updates.Count} package{(updates.Count == 1 ? "" : "s")}.");
    }

    private bool CanUpdateAll() => !IsBusy && UpdatesCount > 0;

    /// <summary>Run a dotnet change, report it, then re-read the installed packages and the list.</summary>
    private async Task ChangeAsync(string what, Func<CancellationToken, Task<NuGetChangeResult>> change, string done)
    {
        IsBusy = true;
        ActionError = null;
        _messenger.Send(new StatusMessage($"NuGet: {what}..."));
        try
        {
            var result = await change(_lifetime.Token);
            var error = result.Succeeded ? null : ErrorLines(result.Output);

            // Busy until the list shows the change (and the error isn't cleared by the reselection).
            await RefreshAsync();
            ActionError = error;
            _messenger.Send(new StatusMessage(result.Succeeded
                ? $"NuGet: {done}"
                : $"NuGet: {what} failed (details in the NuGet tab)."));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ActionError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The error lines of dotnet's output (it's chatty), or all of it when none are marked.</summary>
    private static string ErrorLines(string output)
    {
        var errors = output.Split('\n').Select(line => line.Trim())
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
        return errors.Count > 0 ? string.Join(Environment.NewLine, errors) : output.Trim();
    }
}
