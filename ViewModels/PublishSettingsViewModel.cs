using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Publishing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Visual Studio's Profile settings dialog (Views/PublishSettingsWindow, the Publish page's "Show all settings"):
///
///   Configuration     [Release v]           Target runtime   [Portable v]
///   Target framework  [net10.0 v]           Target location  [bin/Release/net10.0/publish/] [...]
///   Deployment mode   [Framework-dependent v]
///   File publish options: [ ] Delete all existing files  [ ] Produce single file  [ ] ReadyToRun  [ ] Trim
///
/// The rules are VS's: self-contained needs a runtime (choosing it picks this machine's), Portable is always
/// framework-dependent; single file and ReadyToRun need a runtime, trimming a self-contained app.
/// <see cref="Accept"/> gives the edited profile.
/// </summary>
public sealed partial class PublishSettingsViewModel : ObservableObject
{
    private readonly PublishProfile _profile;
    private readonly string _projectPath;

    public PublishSettingsViewModel(string projectPath, PublishProfile profile, IReadOnlyList<string> targetFrameworks)
    {
        _profile = profile;
        _projectPath = projectPath;
        TargetFrameworks = profile.TargetFramework is { } own && !targetFrameworks.Contains(own)
            ? [own, .. targetFrameworks]
            : targetFrameworks;

        Configuration = profile.Configuration;
        TargetFramework = profile.TargetFramework ?? TargetFrameworks.FirstOrDefault();
        Runtime = profile.RuntimeIdentifier ?? PublishTargets.Portable;
        DeploymentMode = profile.SelfContained && !profile.IsPortable ? PublishTargets.SelfContainedMode : PublishTargets.FrameworkDependent;
        Location = profile.PublishDir;
        DeleteExistingFiles = profile.DeleteExistingFiles;
        PublishSingleFile = profile.PublishSingleFile;
        PublishReadyToRun = profile.PublishReadyToRun;
        PublishTrimmed = profile.PublishTrimmed;
    }

    public string ProfileName => _profile.Name;
    public string TargetName => PublishTargets.DisplayName(_profile.TargetId);

    public IReadOnlyList<string> Configurations => PublishTargets.Configurations;
    public IReadOnlyList<string> TargetFrameworks { get; }
    public IReadOnlyList<string> DeploymentModes => PublishTargets.DeploymentModes;
    public IReadOnlyList<string> Runtimes => PublishTargets.Runtimes;

    /// <summary>The framework box only shows when the project's framework is known.</summary>
    public bool HasTargetFrameworks => TargetFrameworks.Count > 0;

    [ObservableProperty] public partial string Configuration { get; set; }
    [ObservableProperty] public partial string? TargetFramework { get; set; }
    [ObservableProperty] public partial string DeploymentMode { get; set; }
    [ObservableProperty] public partial string Runtime { get; set; }
    [ObservableProperty] public partial string Location { get; set; }
    [ObservableProperty] public partial bool DeleteExistingFiles { get; set; }
    [ObservableProperty] public partial bool PublishSingleFile { get; set; }
    [ObservableProperty] public partial bool PublishReadyToRun { get; set; }
    [ObservableProperty] public partial bool PublishTrimmed { get; set; }

    /// <summary>A runtime is chosen (not Portable): single file and ReadyToRun can be turned on.</summary>
    public bool IsRuntimeSpecific => Runtime != PublishTargets.Portable;

    public bool IsSelfContained => DeploymentMode == PublishTargets.SelfContainedMode;

    public string Error => Location.Trim().Length == 0 ? "Enter the folder to publish to." : "";
    public bool CanSave => Error.Length == 0;

    partial void OnRuntimeChanged(string value)
    {
        if (!IsRuntimeSpecific)
        {
            DeploymentMode = PublishTargets.FrameworkDependent;
            PublishSingleFile = false;
            PublishReadyToRun = false;
        }
        OnPropertyChanged(nameof(IsRuntimeSpecific));
    }

    partial void OnDeploymentModeChanged(string value)
    {
        if (IsSelfContained && !IsRuntimeSpecific)
            Runtime = Runtimes.Contains(PublishTargets.CurrentRuntime) ? PublishTargets.CurrentRuntime : "linux-x64";
        if (!IsSelfContained)
            PublishTrimmed = false;
        OnPropertyChanged(nameof(IsSelfContained));
    }

    partial void OnLocationChanged(string value)
    {
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>A folder picked with Browse: relative when it's inside the project.</summary>
    public void UsePickedFolder(string folder) => Location = PublishProfileStore.LocationFor(_projectPath, folder);

    /// <summary>The profile with these settings, or null while they aren't valid.</summary>
    public PublishProfile? Accept() => !CanSave ? null : _profile with
    {
        Configuration = Configuration,
        TargetFramework = TargetFramework,
        RuntimeIdentifier = IsRuntimeSpecific ? Runtime : null,
        SelfContained = IsSelfContained,
        PublishDir = Location.Trim(),
        DeleteExistingFiles = DeleteExistingFiles,
        PublishSingleFile = IsRuntimeSpecific && PublishSingleFile,
        PublishReadyToRun = IsRuntimeSpecific && PublishReadyToRun,
        PublishTrimmed = IsSelfContained && PublishTrimmed
    };
}
