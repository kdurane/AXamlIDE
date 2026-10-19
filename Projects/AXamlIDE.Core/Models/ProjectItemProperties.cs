using System.Collections.Generic;

namespace AXamlIDE.Models;

/// <summary>
/// A file's properties in a project, as Visual Studio's Properties window shows them.
/// </summary>
/// <param name="ItemType">The build action (Compile, None, AvaloniaResource...), or null if the project doesn't include the file.</param>
/// <param name="AvailableItemTypes">The build actions that can be chosen.</param>
/// <param name="Metadata">Evaluated metadata (CopyToOutputDirectory, Generator...); "" when not set.</param>
public sealed record ProjectItemProperties(
    string? ItemType,
    IReadOnlyList<string> AvailableItemTypes,
    IReadOnlyDictionary<string, string> Metadata)
{
    public string MetadataOrEmpty(string name) => Metadata.TryGetValue(name, out var value) ? value : "";
}
