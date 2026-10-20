using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Publishing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>One entry on the wizard's first page: a publish target, or Import Profile (no target).</summary>
public sealed record PublishTargetChoice(PublishTarget? Target, string Name, string Description, string Icon)
{
    public bool IsImport => Target is null;
}

/// <summary>
/// Visual Studio's Publish wizard (Views/PublishWizardWindow), the one "Add a publish profile" opens:
///
///   Target      Where are you publishing today?   [Folder] [Import Profile] (and any later PublishTargets)
///   Location    Folder location: [bin/Release/net10.0/publish/] [Browse...]
///
/// Back / Next / Finish / Cancel along the bottom. ClickOnce isn't offered (VS's Windows-only installer).
/// Finish is allowed once <see cref="Validate"/> is happy; <see cref="Result"/> is then what to create.
/// </summary>
public sealed partial class PublishWizardViewModel : ObservableObject
{
    private readonly string _projectPath;

    public PublishWizardViewModel(string projectPath, string? targetFramework)
    {
        _projectPath = projectPath;
        ProjectName = Path.GetFileNameWithoutExtension(projectPath);
        Targets =
        [
            .. PublishTargets.All.Select(target => new PublishTargetChoice(target, target.Name, target.Description, target.Icon)),
            new PublishTargetChoice(null, "Import Profile", "Import your publish settings from a .pubxml file.", "Icon.OpenInApp")
        ];
        SelectedTarget = Targets[0];
        Location = PublishProfileStore.DefaultLocation(targetFramework);
    }

    public string ProjectName { get; }
    public IReadOnlyList<PublishTargetChoice> Targets { get; }

    [ObservableProperty] public partial PublishTargetChoice? SelectedTarget { get; set; }

    /// <summary>On the second page (Location / Profile), not the first (Target).</summary>
    [ObservableProperty] public partial bool IsSecondStep { get; set; }

    /// <summary>The folder to publish to: relative to the project, or a full path.</summary>
    [ObservableProperty] public partial string Location { get; set; }

    /// <summary>Import Profile: the .pubxml to copy in.</summary>
    [ObservableProperty] public partial string ImportPath { get; set; } = "";

    /// <summary>Why Finish isn't allowed yet ("" when it is, or on the first page).</summary>
    [ObservableProperty] public partial string Error { get; set; } = "";

    public PublishWizardResult? Result { get; private set; }

    public bool IsImport => SelectedTarget?.IsImport == true;
    public bool IsFirstStep => !IsSecondStep;
    public bool ShowsLocation => IsSecondStep && !IsImport;
    public bool ShowsImport => IsSecondStep && IsImport;

    /// <summary>The second step's name in the list on the left, as VS names it.</summary>
    public string SecondStepName => IsImport ? "Profile" : "Location";

    public string Heading => !IsSecondStep
        ? "Where are you publishing today?"
        : IsImport ? "Choose a publish profile to import" : "Where do you want to publish to?";

    public string LocationPrompt => SelectedTarget?.Target?.LocationPrompt ?? "Folder location";

    public bool CanFinish => IsSecondStep && Error.Length == 0;

    partial void OnSelectedTargetChanged(PublishTargetChoice? value)
    {
        OnPropertyChanged(nameof(IsImport));
        OnPropertyChanged(nameof(SecondStepName));
        OnPropertyChanged(nameof(LocationPrompt));
        OnPropertyChanged(nameof(ShowsLocation));
        OnPropertyChanged(nameof(ShowsImport));
        OnPropertyChanged(nameof(Heading));
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSecondStepChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(ShowsLocation));
        OnPropertyChanged(nameof(ShowsImport));
        OnPropertyChanged(nameof(Heading));
        Validate();
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnLocationChanged(string value) => Validate();
    partial void OnImportPathChanged(string value) => Validate();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(CanFinish));

    [RelayCommand(CanExecute = nameof(IsSecondStep))]
    private void Back() => IsSecondStep = false;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => IsSecondStep = true;

    private bool CanGoNext() => IsFirstStep && SelectedTarget is not null;

    /// <summary>A folder picked with Browse: kept relative when it's inside the project, as VS's default is.</summary>
    public void UsePickedFolder(string folder) => Location = PublishProfileStore.LocationFor(_projectPath, folder);

    /// <summary>Finish: true (and <see cref="Result"/> set) when everything's filled in.</summary>
    public bool Accept()
    {
        Validate();
        if (!CanFinish)
            return false;
        Result = IsImport
            ? new PublishWizardResult(null, null, ImportPath.Trim())
            : new PublishWizardResult(SelectedTarget!.Target, Location.Trim(), null);
        return true;
    }

    private void Validate()
    {
        if (!IsSecondStep)
            Error = "";
        else if (IsImport)
            Error = ImportPath.Trim() switch
            {
                "" => "Choose the .pubxml file to import.",
                var path when !path.EndsWith(PublishProfileStore.Extension, StringComparison.OrdinalIgnoreCase) => "A publish profile is a .pubxml file.",
                var path when !File.Exists(path) => "That file doesn't exist.",
                _ => ""
            };
        else
            Error = Location.Trim() switch
            {
                "" => "Enter the folder to publish to.",
                var path when path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 => "That isn't a valid folder path.",
                _ => ""
            };
    }
}
