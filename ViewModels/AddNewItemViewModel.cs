using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>A template in the Add New Item list, with the icon its main file gets in the explorer.</summary>
public sealed record ItemTemplateChoice(ItemTemplate Template)
{
    public string Name => Template.Name;
    public string Category => Template.Category;
    public string Description => Template.Description;
    public string Icon => ProjectTreeNode.FileIcon(Template.DefaultName + Template.Extension);
}

/// <summary>
/// Visual Studio's Add New Item dialog (Views/AddNewItemWindow): categories on the left, the templates in the middle
/// (with a search box), the chosen one's description on the right, its name at the bottom. Add is allowed once
/// <see cref="ItemTemplates.Plan"/> accepts the name; <see cref="Files"/> are then what to write.
/// </summary>
public sealed partial class AddNewItemViewModel : ObservableObject
{
    public const string AllCategories = "All";

    private readonly IReadOnlyList<ItemTemplateChoice> _all = [.. ItemTemplates.All.Select(template => new ItemTemplateChoice(template))];

    /// <param name="folder">Where the item goes.</param>
    /// <param name="workspaceRoot">For showing the folder relative to the workspace.</param>
    /// <param name="templateName">The template to start on (Add > Class...), else Class.</param>
    public AddNewItemViewModel(string folder, string? workspaceRoot, string? templateName = null)
    {
        Folder = folder;
        var relative = workspaceRoot is null ? folder : Path.GetRelativePath(workspaceRoot, folder);
        Location = relative == "." ? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) : relative;
        Namespace = ItemTemplates.NamespaceFor(folder);
        Categories = [AllCategories, .. ItemTemplates.Categories];
        Filter();
        SelectedTemplate = _all.FirstOrDefault(choice => choice.Name == (templateName ?? "Class"));
    }

    public string Folder { get; }

    /// <summary>The folder relative to the workspace, shown under the name box.</summary>
    public string Location { get; }

    /// <summary>The namespace C# items get (the project's root namespace plus the folders).</summary>
    public string Namespace { get; }

    public IReadOnlyList<string> Categories { get; }
    public ObservableCollection<ItemTemplateChoice> Templates { get; } = [];

    [ObservableProperty] public partial string SelectedCategory { get; set; } = AllCategories;
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial ItemTemplateChoice? SelectedTemplate { get; set; }
    [ObservableProperty] public partial string ItemName { get; set; } = "";

    /// <summary>Why the name can't be used yet ("" when it can).</summary>
    [ObservableProperty] public partial string Error { get; set; } = "";

    [ObservableProperty] public partial bool CanAdd { get; set; }

    /// <summary>The files to write, set by <see cref="Accept"/>.</summary>
    public IReadOnlyList<NewItemFile> Files { get; private set; } = [];

    partial void OnSelectedCategoryChanged(string value) => Filter();
    partial void OnSearchChanged(string value) => Filter();

    partial void OnSelectedTemplateChanged(ItemTemplateChoice? value)
    {
        // Like VS: picking a template renames the item to that template's default until you type a name yourself.
        if (value is not null && (ItemName.Length == 0 || _all.Any(choice => ItemName == DefaultFileName(choice))))
            ItemName = DefaultFileName(value);
        Validate();
    }

    partial void OnItemNameChanged(string value) => Validate();

    /// <summary>Checks the name a last time and keeps the files to write; false (with <see cref="Error"/>) if it can't.</summary>
    public bool Accept()
    {
        Validate();
        return CanAdd;
    }

    private static string DefaultFileName(ItemTemplateChoice choice) => choice.Template.DefaultName + choice.Template.Extension;

    private void Filter()
    {
        var selected = SelectedTemplate;
        Templates.Clear();
        foreach (var choice in _all.Where(choice =>
                     (SelectedCategory == AllCategories || choice.Category == SelectedCategory)
                     && (Search.Length == 0 || choice.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                            || choice.Description.Contains(Search, StringComparison.OrdinalIgnoreCase))))
            Templates.Add(choice);
        SelectedTemplate = selected is not null && Templates.Contains(selected) ? selected : Templates.FirstOrDefault();
    }

    private void Validate()
    {
        if (SelectedTemplate is null)
        {
            (Files, Error, CanAdd) = ([], "Pick a template.", false);
            return;
        }
        var (files, error) = ItemTemplates.Plan(SelectedTemplate.Template, Folder, ItemName);
        (Files, Error, CanAdd) = (files, error ?? "", error is null);
    }
}
