using AXamlIDE.MsBuildHost;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

/// <summary>
/// Reads and changes one file's build action (its item type) and metadata, as Visual Studio's Properties window
/// does, keeping the project file's formatting. SDK-style projects include most files through globs, so a file
/// often has no element of its own; then:
///
///   metadata          goes on an Update element:   &lt;None Update="a.txt"&gt;&lt;CopyToOutputDirectory&gt;...
///   build action      Remove from the old type's glob and Include as the new type:
///                     &lt;Compile Remove="a.cs" /&gt; &lt;None Include="a.cs" /&gt;
///
/// A file's own Include/Update elements are edited (or removed) instead of adding more, and changing back
/// undoes the Remove/Include pair rather than piling up elements.
/// </summary>
static class ItemProperties
{
    /// <summary>Build actions always offered, in the order a file's type is looked for.</summary>
    private static readonly string[] CommonTypes =
    [
        "Compile", "AvaloniaXaml", "AvaloniaResource", "EmbeddedResource", "Content", "AdditionalFiles", "None"
    ];

    /// <summary>The metadata the Properties panel shows.</summary>
    private static readonly string[] MetadataNames =
    [
        "CopyToOutputDirectory", "CopyToPublishDirectory", "Generator", "CustomToolNamespace", "Link", "DependentUpon", "Visible"
    ];

    public static HostItem Get(string projectPath, string filePath)
    {
        using var collection = new ProjectCollection();
        var project = Load(collection, projectPath, out _);
        return Describe(project, filePath);
    }

    public static HostItem Set(string projectPath, string filePath, string? itemType, string? metadataName, string? metadataValue)
    {
        using var collection = new ProjectCollection();
        var project = Load(collection, projectPath, out var root);
        var relative = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, filePath).Replace('\\', '/');

        if (!string.IsNullOrEmpty(itemType))
            ChangeType(project, root, filePath, relative, itemType);
        if (!string.IsNullOrEmpty(metadataName))
            SetMetadata(project, root, filePath, relative, metadataName, metadataValue ?? "");

        if (root.HasUnsavedChanges)
            root.Save();
        project.ReevaluateIfNecessary();
        return Describe(project, filePath);
    }

    private static Project Load(ProjectCollection collection, string projectPath, out ProjectRootElement root)
    {
        root = ProjectRootElement.Open(projectPath, collection, preserveFormatting: true);
        return new Project(root, null, null, collection);
    }

    private static HostItem Describe(Project project, string filePath)
    {
        var item = ItemFor(project, filePath);
        var available = CommonTypes
            .Concat(project.GetItems("AvailableItemName").Select(name => name.EvaluatedInclude).Order(StringComparer.Ordinal))
            .Append(item?.ItemType ?? "")
            .Where(type => type.Length > 0 && !type.StartsWith('_'))
            .Distinct()
            .ToArray();
        var metadata = MetadataNames.ToDictionary(name => name, name => item?.GetMetadataValue(name) ?? "");
        return new HostItem(item?.ItemType, available, metadata);
    }

    /// <summary>
    /// The evaluated item for the file: of a common type first, else any type offered as a build action. A file can
    /// be two types at once when the SDK adds a hidden one (Avalonia makes $(ApplicationIcon) a Visible="false"
    /// AvaloniaResource for the default window icon); the one the explorer shows comes first.
    /// </summary>
    private static ProjectItem? ItemFor(Project project, string filePath)
    {
        var items = CommonTypes
            .Concat(project.GetItems("AvailableItemName").Select(name => name.EvaluatedInclude))
            .Distinct()
            .Select(type => ItemOfType(project, type, filePath))
            .OfType<ProjectItem>()
            .ToList();
        return items.FirstOrDefault(item => !IsHidden(item)) ?? items.FirstOrDefault();
    }

    private static bool IsHidden(ProjectItem item)
        => string.Equals(item.GetMetadataValue("Visible"), "false", StringComparison.OrdinalIgnoreCase);

    private static ProjectItem? ItemOfType(Project project, string type, string filePath)
        => project.GetItems(type)
            .Where(item => SameFullPath(item.GetMetadataValue("FullPath"), filePath))
            .OrderBy(IsHidden)
            .FirstOrDefault();

    // ------------------------------------------------------------- build action

    private static void ChangeType(Project project, ProjectRootElement root, string filePath, string relative, string newType)
    {
        var oldType = ItemFor(project, filePath)?.ItemType;
        if (oldType == newType)
            return;

        // The file's own elements of the old type go; their metadata moves to the new type.
        var carried = new List<(string Name, string Value)>();
        if (oldType is not null)
        {
            foreach (var element in ElementsFor(root, oldType, relative, include: true, update: true))
            {
                carried.AddRange(element.Metadata.Select(metadata => (metadata.Name, metadata.Value)));
                Delete(element);
            }
        }

        // Undo an earlier "removed from the new type's glob".
        foreach (var element in root.Items.Where(element => element.ItemType == newType && SamePath(element.Remove, relative)).ToList())
            Delete(element);

        project.ReevaluateIfNecessary();
        if (oldType is not null && ItemOfType(project, oldType, filePath) is not null)
        {
            // Still included by a glob: remove it from that. An item added after the project file is read (in the
            // SDK's .targets) can't be removed this way, so a Remove that changes nothing goes again.
            var remove = Element(root, oldType, remove: relative);
            AppendToGroup(root, remove);
            project.ReevaluateIfNecessary();
            if (ItemOfType(project, oldType, filePath) is not null)
                Delete(remove);
        }

        project.ReevaluateIfNecessary();
        if (ItemOfType(project, newType, filePath) is null)
            root.AddItem(newType, relative);

        project.ReevaluateIfNecessary();
        foreach (var (name, value) in carried)
            SetMetadata(project, root, filePath, relative, name, value);
    }

    // ------------------------------------------------------------- metadata

    private static void SetMetadata(Project project, ProjectRootElement root, string filePath, string relative, string name, string value)
    {
        if (ItemFor(project, filePath) is not { } item)
            return;
        var type = item.ItemType;

        if (value.Length == 0)
        {
            // Clear our own value; if a default still applies (e.g. from a glob), say "no" explicitly.
            foreach (var element in OwnElements(root, type, relative))
            {
                foreach (var metadata in element.Metadata.Where(metadata => metadata.Name == name).ToList())
                    element.RemoveChild(metadata);
                if (element.Update.Length > 0 && element.Count == 0)
                    Delete(element);
            }

            project.ReevaluateIfNecessary();
            if (ItemFor(project, filePath)?.GetMetadataValue(name) is not { Length: > 0 })
                return;
            value = name.StartsWith("CopyTo", StringComparison.Ordinal) ? "Never" : "";
        }

        var target = OwnElements(root, type, relative).FirstOrDefault();
        if (target is null)
        {
            target = Element(root, type, update: relative);
            AppendToGroup(root, target);
        }

        if (target.Metadata.FirstOrDefault(metadata => metadata.Name == name) is { } existing)
            existing.Value = value;
        else
            target.AddMetadata(name, value);
    }

    /// <summary>The file's own Include element of the type, else its Update element.</summary>
    private static IEnumerable<ProjectItemElement> OwnElements(ProjectRootElement root, string type, string relative)
        => ElementsFor(root, type, relative, include: true, update: false)
            .Concat(ElementsFor(root, type, relative, include: false, update: true));

    private static List<ProjectItemElement> ElementsFor(ProjectRootElement root, string type, string relative, bool include, bool update)
        => root.Items
            .Where(element => element.ItemType == type
                              && ((include && SamePath(element.Include, relative)) || (update && SamePath(element.Update, relative))))
            .ToList();

    // ------------------------------------------------------------- elements

    private static ProjectItemElement Element(ProjectRootElement root, string type, string? update = null, string? remove = null)
    {
        var element = root.CreateItemElement(type);
        if (update is not null)
            element.Update = update;
        if (remove is not null)
            element.Remove = remove;
        return element;
    }

    /// <summary>Into the last plain ItemGroup that already holds this type, else a new one.</summary>
    private static void AppendToGroup(ProjectRootElement root, ProjectItemElement element)
    {
        var group = root.ItemGroups.LastOrDefault(group => group.Condition.Length == 0 && group.Items.Any(item => item.ItemType == element.ItemType))
                    ?? root.AddItemGroup();
        group.AppendChild(element);
    }

    /// <summary>Remove an element, and its ItemGroup if that leaves it empty.</summary>
    internal static void Delete(ProjectElement element)
    {
        var parent = element.Parent;
        parent.RemoveChild(element);
        if (parent is ProjectItemGroupElement { Count: 0 } group)
            group.Parent.RemoveChild(group);
    }

    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Two full paths (an evaluated item's FullPath and the file).</summary>
    private static bool SameFullPath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);

    /// <summary>A path as written in an Include/Update/Remove attribute, and the file's path relative to the project.</summary>
    private static bool SamePath(string attribute, string relative)
        => attribute.Length > 0 && string.Equals(Normalize(attribute), Normalize(relative), PathComparison);

    /// <summary>A path as written in the project: forward slashes, no leading "./".</summary>
    internal static string Normalize(string path)
    {
        path = path.Trim().Replace('\\', '/');
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }
}
