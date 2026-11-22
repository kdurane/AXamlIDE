using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.ViewModels;

/// <summary>One unsaved file in the save prompt: its name, and the folder it's in (dimmed).</summary>
public sealed record SaveChangesItem(string Name, string Folder);

/// <summary>
/// The save prompt (Views/SaveChangesWindow), as in Visual Studio: one question for every unsaved file being
/// closed, "Save changes to X?" for one, or a list of them under "Save changes to the following items?".
/// </summary>
public sealed class SaveChangesViewModel(IReadOnlyList<string> paths)
{
    public string Prompt => paths.Count == 1
        ? $"Save changes to {Path.GetFileName(paths[0])}?"
        : "Save changes to the following items?";

    public bool ShowList => paths.Count > 1;

    public IReadOnlyList<SaveChangesItem> Items { get; } = paths
        .Select(path => new SaveChangesItem(Path.GetFileName(path), Path.GetDirectoryName(path) ?? ""))
        .ToList();
}
