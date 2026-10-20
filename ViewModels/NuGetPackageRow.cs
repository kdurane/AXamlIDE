using System;
using System.Linq;
using AXamlIDE.Services.NuGet;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One line of the NuGet manager's list: a search result (Browse) or an installed package (Installed / Updates).
/// Installed and latest versions are filled in as they're known; the icon loads in the background.
/// </summary>
public sealed partial class NuGetPackageRow(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] public partial string? Authors { get; set; }
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial long? TotalDownloads { get; set; }
    [ObservableProperty] public partial bool IsVerified { get; set; }
    [ObservableProperty] public partial string? IconUrl { get; set; }
    [ObservableProperty] public partial byte[]? IconData { get; set; }

    /// <summary>Ticked for "Update Selected" (the Updates tab's check boxes).</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }

    /// <summary>The version the project has (resolved by restore), or null when it isn't installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(HasUpdate), nameof(VersionText))]
    public partial string? InstalledVersion { get; set; }

    /// <summary>The newest version on the sources (stable unless prereleases are included).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(VersionText))]
    public partial string? LatestVersion { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    public bool HasUpdate => NuGetVersion.TryParse(InstalledVersion) is { } installed
                             && NuGetVersion.TryParse(LatestVersion) is { } latest
                             && latest > installed;

    /// <summary>What the list shows on the right: "1.2.0", or "1.2.0 → 1.3.0" when there's an update.</summary>
    public string VersionText => HasUpdate ? $"{InstalledVersion} → {LatestVersion}" : InstalledVersion ?? LatestVersion ?? "";

    /// <summary>The description's first line (the list shows two lines at most).</summary>
    public string ShortDescription => Description?.Split('\n').FirstOrDefault()?.Trim() ?? "";

    public string DownloadsText => TotalDownloads switch
    {
        null => "",
        >= 1_000_000_000 => $"{TotalDownloads / 1_000_000_000.0:0.#}B downloads",
        >= 1_000_000 => $"{TotalDownloads / 1_000_000.0:0.#}M downloads",
        >= 1_000 => $"{TotalDownloads / 1_000.0:0.#}K downloads",
        _ => $"{TotalDownloads} downloads"
    };

    partial void OnDescriptionChanged(string? value) => OnPropertyChanged(nameof(ShortDescription));
    partial void OnTotalDownloadsChanged(long? value) => OnPropertyChanged(nameof(DownloadsText));

    public static NuGetPackageRow From(NuGetSearchResult result) => new(result.Id)
    {
        Authors = result.Authors,
        Description = result.Description,
        TotalDownloads = result.TotalDownloads,
        IsVerified = result.IsVerified,
        IconUrl = result.IconUrl,
        LatestVersion = result.Version
    };
}
