using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Visual Studio's "Create a new project" (Views/NewProjectWindow), in two steps:
///   1. choose a template: the installed <c>dotnet new</c> project templates, Avalonia's first, with a search box
///      (and a button to install Avalonia's templates when they're missing);
///   2. configure it: project name, location, solution name, and "Place solution and project in the same directory".
/// Create runs <c>dotnet new</c> and makes the solution; <see cref="Done"/> is raised with the solution's path.
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private IReadOnlyList<ProjectTemplate> _all = [];
    private CancellationTokenSource? _running;
    private bool _solutionNameEdited;
    private bool _settingSolutionName;

    public NewProjectViewModel(string defaultLocation, IDialogService dialogs)
    {
        _dialogs = dialogs;
        Location = defaultLocation;
    }

    // ------------------------------------------------------------- step 1: the template

    public ObservableCollection<ProjectTemplate> Templates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial ProjectTemplate? SelectedTemplate { get; set; }

    [ObservableProperty] public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingTemplate), nameof(IsConfiguring))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(CreateCommand))]
    public partial int Step { get; set; } = 1;

    public bool IsChoosingTemplate => Step == 1;
    public bool IsConfiguring => Step == 2;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(CreateCommand), nameof(InstallAvaloniaTemplatesCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>No Avalonia templates are installed: offer to install them.</summary>
    [ObservableProperty] public partial bool AvaloniaTemplatesMissing { get; set; }

    /// <summary>What's happening (loading, creating: dotnet's latest line), or what went wrong.</summary>
    [ObservableProperty] public partial string Status { get; set; } = "";

    [ObservableProperty] public partial bool StatusIsError { get; set; }

    partial void OnSearchChanged(string value) => Filter();

    /// <summary>List the installed templates (when the window opens).</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusIsError = false;
        Status = "Loading the installed templates...";
        var templates = await ProjectTemplates.ListAsync(CancellationToken.None);
        IsBusy = false;
        if (templates is null)
        {
            ShowError("Couldn't list the project templates. Is the .NET SDK installed (dotnet on the PATH)?");
            return;
        }

        // Avalonia's first (this IDE's focus), then the rest by name.
        _all = [.. templates.OrderByDescending(template => template.IsAvalonia).ThenBy(template => template.Name, StringComparer.OrdinalIgnoreCase)];
        AvaloniaTemplatesMissing = !_all.Any(template => template.IsAvalonia);
        Status = "";
        Filter();
    }

    private void Filter()
    {
        var search = Search.Trim();
        var selected = SelectedTemplate;
        Templates.Clear();
        foreach (var template in _all.Where(template => search.Length == 0
                     || template.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || template.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || template.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase))))
            Templates.Add(template);
        SelectedTemplate = selected is not null && Templates.Contains(selected) ? selected : Templates.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAvaloniaTemplates()
    {
        if (!await _dialogs.ConfirmAsync("Install Avalonia Templates",
                $"Install Avalonia's project templates for dotnet new?\n\nThis runs: dotnet new install {ProjectTemplates.AvaloniaTemplatesPackage}",
                "Install"))
            return;

        IsBusy = true;
        StatusIsError = false;
        Status = "Installing the Avalonia templates...";
        try
        {
            await ProjectTemplates.InstallAvaloniaTemplatesAsync(ShowProgress, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            IsBusy = false;
            ShowError($"Couldn't install the templates: {ex.Message}");
            return;
        }
        IsBusy = false;
        await LoadAsync();
    }

    private bool CanInstall => !IsBusy;

    private bool CanGoNext => !IsBusy && Step == 1 && SelectedTemplate is not null;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (SelectedTemplate is not { } template)
            return;
        ProjectName = FirstFreeName(template);
        Step = 2;
        Status = "";
        StatusIsError = false;
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy)
            return;
        Step = 1;
        Status = "";
    }

    // ------------------------------------------------------------- step 2: configure

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination))]
    public partial string ProjectName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination))]
    public partial string Location { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination))]
    public partial string SolutionName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination), nameof(AskSolutionName))]
    public partial bool SolutionInProjectFolder { get; set; }

    /// <summary>The solution name box: not when the solution goes in the project's folder (it takes the project's name).</summary>
    public bool AskSolutionName => !SolutionInProjectFolder;

    /// <summary>Visual Studio's "Project will be created in ..." line, or what's wrong with the names so far.</summary>
    public string Destination => Request() is not { } request
        ? ""
        : ProjectTemplates.Problem(request) ?? $"Project will be created in \"{request.ProjectFolder}\"";

    partial void OnProjectNameChanged(string value)
    {
        if (_solutionNameEdited)
            return;
        _settingSolutionName = true;
        SolutionName = value;
        _settingSolutionName = false;
    }

    partial void OnSolutionNameChanged(string value)
    {
        if (!_settingSolutionName)
            _solutionNameEdited = true;
    }

    [RelayCommand]
    private async Task BrowseLocation()
    {
        if (await _dialogs.PickFolderAsync() is { } folder)
            Location = folder;
    }

    /// <summary>The solution was created: its path.</summary>
    public event Action<string>? Done;

    private bool CanCreate => !IsBusy && Step == 2;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        if (Request() is not { } request)
            return;
        if (ProjectTemplates.Problem(request) is { } problem)
        {
            ShowError(problem);
            return;
        }

        _running = new CancellationTokenSource();
        IsBusy = true;
        StatusIsError = false;
        Status = $"Creating {request.ProjectName}...";
        try
        {
            var solution = await ProjectTemplates.CreateAsync(request, ShowProgress, _running.Token);
            Done?.Invoke(solution);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Anything already created was left in place.";
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            _running.Dispose();
            _running = null;
        }
    }

    /// <summary>Stop a create that's running (the window's Cancel while busy); false when none is.</summary>
    public bool CancelCreate()
    {
        if (_running is null)
            return false;
        _running.Cancel();
        return true;
    }

    private NewProjectRequest? Request() => SelectedTemplate is { } template
        ? new NewProjectRequest(template, ProjectName, Location.Trim(), SolutionName, SolutionInProjectFolder)
        : null;

    /// <summary>"AvaloniaApp1", "ConsoleApp1"... as Visual Studio names a new project: the first one not taken in the location.</summary>
    private string FirstFreeName(ProjectTemplate template)
    {
        var stem = template.IsAvalonia
            ? "AvaloniaApp"
            : new string([.. template.Name.Where(char.IsLetterOrDigit)]) is { Length: > 0 } letters ? letters : "Project";
        for (var number = 1; ; number++)
        {
            var name = stem + number;
            if (!Directory.Exists(Path.Combine(Location, name)))
                return name;
        }
    }

    private void ShowProgress(string line)
    {
        if (line.Trim().Length > 0)
            Dispatcher.UIThread.Post(() => { if (IsBusy) Status = line.Trim(); });
    }

    private void ShowError(string message)
    {
        StatusIsError = true;
        Status = message;
    }
}
