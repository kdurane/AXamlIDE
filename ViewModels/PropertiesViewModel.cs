using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One property in the Properties panel: read-only text, a text box, or a drop-down. Editing it applies
/// the change at once (as Visual Studio does).
/// </summary>
public sealed partial class PropertyRow : ObservableObject
{
    private readonly Func<string, Task>? _apply;
    private readonly bool _constructed;   // the first value is what's there already, not a change to apply

    public PropertyRow(string name, string value, string description, IReadOnlyList<string>? choices = null, Func<string, Task>? apply = null)
    {
        Name = name;
        Description = description;
        Choices = choices;
        _apply = apply;
        Value = value;
        _constructed = true;
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<string>? Choices { get; }

    public bool IsReadOnly => _apply is null;
    public bool IsChoice => _apply is not null && Choices is not null;
    public bool IsText => _apply is not null && Choices is null;

    [ObservableProperty] public partial string Value { get; set; }

    partial void OnValueChanged(string value)
    {
        if (_constructed && _apply is not null)
            _ = _apply(value);
    }
}

/// <param name="Name">The category ("Advanced", "Misc"), as in Visual Studio.</param>
public sealed record PropertyGroup(string Name, IReadOnlyList<PropertyRow> Rows);

/// <summary>
/// The Properties panel (under the explorer): the selected file's, folder's or project's properties. For a file
/// in a project that's its Build Action, Copy to Output Directory, Custom Tool..., read from and written to the
/// project through the MSBuild host; every change is written to the .csproj immediately. It follows the explorer's
/// selection while it's open.
/// </summary>
public sealed partial class PropertiesViewModel : ObservableObject
{
    // Copy to Output Directory: what Visual Studio shows -> the metadata value.
    private static readonly (string Display, string Value)[] CopyChoices =
    [
        ("Do not copy", ""),
        ("Copy always", "Always"),
        ("Copy if newer", "PreserveNewest")
    ];

    private readonly WorkspaceViewModel _workspace;
    private readonly WorkspaceModel _model;
    private readonly DockLayoutViewModel _docking;
    private readonly IMessenger _messenger;
    private int _loadVersion;

    public PropertiesViewModel(WorkspaceViewModel workspace, WorkspaceModel model, DockLayoutViewModel docking, IMessenger messenger)
    {
        _workspace = workspace;
        _model = model;
        _docking = docking;
        _messenger = messenger;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    /// <summary>"Program.cs  File Properties".</summary>
    [ObservableProperty] public partial string Heading { get; private set; } = "";

    public ObservableCollection<PropertyGroup> Groups { get; } = [];

    /// <summary>Open the panel (right-click > Properties, F4) for the selected node.</summary>
    [RelayCommand]
    private async Task Show()
    {
        _docking.ShowTool(ToolTab.Properties);
        await LoadAsync(_workspace.SelectedNode);
    }

    private async void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedNode) && _docking.IsToolShown(ToolTab.Properties))
            await LoadAsync(_workspace.SelectedNode);
    }

    // ------------------------------------------------------------- reading

    private async Task LoadAsync(ProjectTreeNode? node)
    {
        var version = ++_loadVersion;
        var (heading, groups) = node is null
            ? ("", new List<PropertyGroup>())
            : await Task.Run(() => Describe(node));
        if (version != _loadVersion)
            return;   // the selection moved on meanwhile

        Heading = heading;
        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(group);
    }

    /// <summary>The heading and properties of a node (runs off the UI thread: it may ask MSBuild).</summary>
    private (string Heading, List<PropertyGroup> Groups) Describe(ProjectTreeNode node)
    {
        switch (node.Kind)
        {
            case NodeKind.File:
                return ($"{node.Name}  File Properties", DescribeFile(node.FullPath));
            case NodeKind.Directory:
                return ($"{node.Name}  Folder Properties",
                    [Misc(ReadOnly("Folder Name", node.Name, "The folder's name."), FullPath(node.FullPath))]);
            case NodeKind.Folder:
                return ($"{node.Name}  Folder Properties", [Misc(ReadOnly("Folder Name", node.Name, "The folder's name."))]);
            case NodeKind.Project:
                return ($"{node.Name}  Project Properties", DescribeProject(node.FullPath));
            case NodeKind.Solution:
                return ($"{node.Name}  Solution Properties",
                    [Misc(ReadOnly("Solution File", Path.GetFileName(node.FullPath), "The solution file."), FullPath(node.FullPath))]);
            default:
                return ("", []);
        }
    }

    private List<PropertyGroup> DescribeFile(string path)
    {
        var misc = Misc(ReadOnly("File Name", Path.GetFileName(path), "The file's name."), FullPath(path));
        if (_model.FindProjectForDocument(path) is not { } project)
            return [misc];

        ProjectItemProperties item;
        try
        {
            item = _model.Host.GetItemProperties(project, path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return [misc, new PropertyGroup("Advanced", [ReadOnly("Build Action", "(couldn't read the project)", ex.Message)])];
        }

        var advanced = new List<PropertyRow>
        {
            new("Build Action", item.ItemType ?? "", "How the file is built: compiled code, an Avalonia resource, embedded, or nothing.",
                item.AvailableItemTypes, value => ApplyAsync(project, path, host => host.SetItemType(project, path, value), $"Build Action: {value}")),
            new("Copy to Output Directory", CopyDisplay(item.MetadataOrEmpty("CopyToOutputDirectory")),
                "Whether the file is copied to the build output (bin) folder.",
                CopyChoices.Select(choice => choice.Display).ToList(),
                display => ApplyAsync(project, path, host => host.SetItemMetadata(project, path, "CopyToOutputDirectory", CopyValue(display)),
                    $"Copy to Output Directory: {display}")),
            Text(project, path, item, "Custom Tool", "Generator", "The tool that generates code from the file (e.g. ResXFileCodeGenerator)."),
            Text(project, path, item, "Custom Tool Namespace", "CustomToolNamespace", "The namespace for the generated code.")
        };
        foreach (var (name, metadata, description) in new[]
                 {
                     ("Link", "Link", "Where a file from outside the project folder appears in the project."),
                     ("Dependent Upon", "DependentUpon", "The file this one is nested under.")
                 })
            if (item.MetadataOrEmpty(metadata) is { Length: > 0 } value)
                advanced.Add(ReadOnly(name, value, description));

        return [new PropertyGroup("Advanced", advanced), misc];
    }

    private List<PropertyGroup> DescribeProject(string projectPath)
    {
        var rows = new List<PropertyRow>
        {
            ReadOnly("Project File", Path.GetFileName(projectPath), "The project file."),
            ReadOnly("Project Folder", Path.GetDirectoryName(projectPath) ?? "", "The folder the project is in.")
        };
        try
        {
            rows.Add(ReadOnly("Assembly Name", _model.Host.GetProjectAssemblyName(projectPath), "The name of the built assembly."));
            rows.Add(ReadOnly("Output Type", _model.Host.GetProjectOutputType(projectPath) ?? "", "Exe, WinExe or Library."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            rows.Add(ReadOnly("MSBuild", "(couldn't read the project)", ex.Message));
        }
        return [Misc([.. rows])];
    }

    private PropertyRow Text(string project, string path, ProjectItemProperties item, string name, string metadata, string description)
        => new(name, item.MetadataOrEmpty(metadata), description, apply: value =>
            ApplyAsync(project, path, host => host.SetItemMetadata(project, path, metadata, value.Trim()), $"{name}: {value.Trim()}"));

    private static PropertyRow ReadOnly(string name, string value, string description) => new(name, value, description);

    private static PropertyRow FullPath(string path) => ReadOnly("Full Path", path, "Where it is on disk.");

    private static PropertyGroup Misc(params PropertyRow[] rows) => new("Misc", rows);

    private static string CopyDisplay(string value)
        => CopyChoices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)).Display
           ?? "Do not copy";   // "Never" or unset

    private static string CopyValue(string display) => CopyChoices.FirstOrDefault(choice => choice.Display == display).Value ?? "";

    // ------------------------------------------------------------- writing

    /// <summary>Write a change to the project, then show the file's properties as they now are.</summary>
    private async Task ApplyAsync(string project, string path, Func<MsBuildHostClient, ProjectItemProperties> change, string what)
    {
        try
        {
            await Task.Run(() => change(_model.Host));
            _messenger.Send(new StatusMessage($"{Path.GetFileName(path)}: {what} (saved to {Path.GetFileName(project)})"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _messenger.Send(new StatusMessage($"Couldn't change {Path.GetFileName(path)}: {ex.Message}"));
        }

        if (_workspace.SelectedNode is { } node && node.FullPath == path)
            await LoadAsync(node);
    }
}
