using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Publishing;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Publish page for one project, as a document tab (Views/PublishView), like Visual Studio 2022's:
///
///   [FolderProfile v] [Publish]   + New profile   ... (Rename, Delete, Open profile file)
///   Ready to publish. / Publish succeeded on ... [Open folder]
///   Settings                                         Show all settings
///     Target, Configuration, Target framework, Deployment mode, Target runtime, Target location
///
/// The profiles are the project's .pubxml files (PublishProfileStore). With none, the page offers "Add a publish
/// profile" and opens the wizard straight away, as VS does. Publishing runs through RunViewModel, so its output is
/// in the Output panel with the builds'. Opened by PublishManager.
/// </summary>
public sealed partial class PublishDockable : PageDockable
{
    private readonly RunViewModel _run;
    private readonly WorkspaceModel _workspace;
    private readonly IDialogService _dialogs;
    private readonly IDocumentHost _documents;
    private readonly IMessenger _messenger;
    private IReadOnlyList<string> _targetFrameworks = [];
    private bool _disposed;

    public PublishDockable(
        string projectPath,
        RunViewModel run,
        WorkspaceModel workspace,
        IDialogService dialogs,
        IDocumentHost documents,
        IMessenger messenger)
    {
        _run = run;
        _workspace = workspace;
        _dialogs = dialogs;
        _documents = documents;
        _messenger = messenger;
        ProjectPath = projectPath;
        ProjectName = Path.GetFileNameWithoutExtension(projectPath);
        Id = IdFor(projectPath);
        Title = $"Publish: {ProjectName}";
        CanPin = false;
        _ = InitializeAsync();
    }

    public static string IdFor(string projectPath) => "publish:" + projectPath;

    public string ProjectPath { get; }
    public string ProjectName { get; }

    public ObservableCollection<PublishProfile> Profiles { get; } = [];

    [ObservableProperty] public partial PublishProfile? SelectedProfile { get; set; }

    [ObservableProperty] public partial bool IsPublishing { get; set; }

    /// <summary>The line under the profile box: ready, publishing, succeeded or failed.</summary>
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    /// <summary>The last publish failed (the status line shows in the error colour).</summary>
    [ObservableProperty] public partial bool LastFailed { get; set; }

    /// <summary>The target folder exists, so Open folder has something to open.</summary>
    [ObservableProperty] public partial bool TargetLocationExists { get; set; }

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasNoProfiles => !HasProfiles;

    // ---- the Settings summary of the selected profile

    public string TargetName => SelectedProfile is { } profile ? PublishTargets.DisplayName(profile.TargetId) : "";
    public string Configuration => SelectedProfile?.Configuration ?? "";
    public string TargetFramework => SelectedProfile?.TargetFramework ?? _targetFrameworks.FirstOrDefault() ?? "Project default";
    public string DeploymentMode => SelectedProfile is { SelfContained: true, IsPortable: false }
        ? PublishTargets.SelfContainedMode
        : PublishTargets.FrameworkDependent;
    public string TargetRuntime => SelectedProfile?.RuntimeIdentifier ?? PublishTargets.Portable;
    public string TargetLocation => SelectedProfile is { } profile ? PublishCommandLine.PublishFolder(ProjectPath, profile) : "";

    /// <summary>The file options that are on, VS-style ("Single file, ReadyToRun"); "" when none.</summary>
    public string FileOptions => SelectedProfile is not { } profile ? "" : string.Join(", ", new[]
    {
        profile.DeleteExistingFiles ? "Delete existing files" : null,
        profile.PublishSingleFile ? "Single file" : null,
        profile.PublishReadyToRun ? "ReadyToRun" : null,
        profile.PublishTrimmed ? "Trimmed" : null
    }.OfType<string>());

    public bool HasFileOptions => FileOptions.Length > 0;

    /// <summary>Why the selected profile can't be published from here (a VS-only target), or "".</summary>
    public string UnsupportedText => SelectedProfile is { } profile && !PublishTargets.CanPublish(profile)
        ? $"AXamlIDE can't publish to {PublishTargets.DisplayName(profile.TargetId)} yet. Use Visual Studio for this profile, or add a Folder profile."
        : "";

    public bool IsUnsupported => UnsupportedText.Length > 0;

    partial void OnSelectedProfileChanged(PublishProfile? value)
    {
        foreach (var name in (string[])[nameof(TargetName), nameof(Configuration), nameof(TargetFramework), nameof(DeploymentMode),
                     nameof(TargetRuntime), nameof(TargetLocation), nameof(FileOptions), nameof(HasFileOptions),
                     nameof(UnsupportedText), nameof(IsUnsupported)])
            OnPropertyChanged(name);
        if (!IsPublishing)
        {
            StatusText = value is null ? "" : IsUnsupported ? "" : "Ready to publish.";
            LastFailed = false;
        }
        RefreshTargetLocation();
        NotifyCommands();
    }

    partial void OnIsPublishingChanged(bool value) => NotifyCommands();

    // ---------------------------------------------------------------- loading

    private async Task InitializeAsync()
    {
        try
        {
            _targetFrameworks = await Task.Run(() => _workspace.Host.GetProjectTargetFrameworks(ProjectPath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _targetFrameworks = [];   // MSBuild couldn't evaluate it; the profiles still work
        }
        OnPropertyChanged(nameof(TargetFramework));

        Reload();
        if (!HasProfiles && !_disposed)
            await NewProfileAsync();   // like VS: a project with no profile goes straight to the wizard
    }

    /// <summary>Read the profiles again, keeping (or choosing) the selection.</summary>
    private void Reload(string? select = null)
    {
        var name = select ?? SelectedProfile?.Name;
        var profiles = PublishProfileStore.Load(ProjectPath);
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Name == name)
                          ?? PublishProfileStore.Initial(ProjectPath, profiles);
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        NotifyCommands();
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task Publish()
    {
        if (SelectedProfile is not { } profile)
            return;

        IsPublishing = true;
        LastFailed = false;
        StatusText = "Publishing...";
        try
        {
            var succeeded = await _run.PublishAsync(ProjectPath, profile);
            LastFailed = !succeeded;
            StatusText = succeeded
                ? $"Publish succeeded on {DateTime.Now:g}."
                : "Publish didn't finish: see the Output panel.";
        }
        finally
        {
            IsPublishing = false;
            RefreshTargetLocation();
        }
    }

    private bool CanPublish() => SelectedProfile is { } profile && PublishTargets.CanPublish(profile) && !IsPublishing;

    /// <summary>"+ New profile" (and "Add a publish profile"): the Publish wizard.</summary>
    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var result = await _dialogs.ShowPublishWizardAsync(ProjectPath, _targetFrameworks.FirstOrDefault());
        if (result is null)
            return;

        try
        {
            PublishProfile profile;
            if (result.ImportPath is { } importPath)
                profile = PublishProfileStore.Import(ProjectPath, importPath);
            else
            {
                profile = PublishProfileStore.NewProfile(ProjectPath, result.Target!, result.Location!, _targetFrameworks.FirstOrDefault());
                PublishProfileStore.Save(profile);
            }
            Reload(profile.Name);
            Status($"Created publish profile {profile.Name} ({Path.GetRelativePath(Path.GetDirectoryName(ProjectPath)!, profile.FilePath)}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Status($"Couldn't create the publish profile: {ex.Message}");
        }
    }

    /// <summary>"Show all settings": VS's Profile settings dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ShowAllSettings()
    {
        if (SelectedProfile is not { } profile)
            return;
        if (await _dialogs.EditPublishProfileAsync(ProjectPath, profile, _targetFrameworks) is not { } edited)
            return;

        try
        {
            PublishProfileStore.Save(edited);
            Reload(edited.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Status($"Couldn't save {profile.Name}: {ex.Message}");
        }
    }

    private bool CanEdit() => SelectedProfile is { } profile && PublishTargets.CanPublish(profile) && !IsPublishing;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameProfile()
    {
        if (SelectedProfile is not { } profile)
            return;
        var name = await _dialogs.PromptForTextAsync("Rename profile", "Profile name", "Rename", profile.Name);
        if (name is null || name == profile.Name)
            return;
        if (PublishProfileStore.ValidateName(ProjectPath, name, profile) is { } error)
        {
            Status(error);
            return;
        }

        try
        {
            Reload(PublishProfileStore.Rename(profile, name).Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't rename {profile.Name}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteProfile()
    {
        if (SelectedProfile is not { } profile)
            return;
        if (!await _dialogs.ConfirmAsync("Delete profile",
                $"Delete the publish profile {profile.Name}? Its .pubxml file is deleted; what it published stays.", "Delete"))
            return;

        try
        {
            PublishProfileStore.Delete(profile);
            SelectedProfile = null;
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't delete {profile.Name}: {ex.Message}");
        }
    }

    /// <summary>Open the .pubxml in the editor, for the settings the page doesn't show.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenProfileFile()
    {
        if (SelectedProfile is { } profile)
            _documents.OpenAndActivate(profile.FilePath);
    }

    private bool HasSelection() => SelectedProfile is not null && !IsPublishing;

    // ---------------------------------------------------------------- helpers

    /// <summary>Check again whether the target folder exists (after a publish, or a change of profile).</summary>
    public void RefreshTargetLocation() => TargetLocationExists = TargetLocation.Length > 0 && Directory.Exists(TargetLocation);

    private void NotifyCommands()
    {
        PublishCommand.NotifyCanExecuteChanged();
        ShowAllSettingsCommand.NotifyCanExecuteChanged();
        RenameProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        OpenProfileFileCommand.NotifyCanExecuteChanged();
    }

    private void Status(string text) => _messenger.Send(new StatusMessage(text));

    public override void Dispose() => _disposed = true;
}
