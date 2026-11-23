using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.ViewModels;

/// <summary>
/// A warning before something that can't be undone (Views/WarningWindow), as VS Code's discard prompts: the
/// question, the files it touches (listed when there are any), and a button per choice, then Cancel.
/// </summary>
/// <param name="files">Paths shown under the message (name, then folder dimmed); empty for none.</param>
/// <param name="choices">The button texts, the safest first; the window closes with the one clicked.</param>
public sealed class WarningViewModel(string message, IReadOnlyList<string> files, IReadOnlyList<string> choices)
{
    public string Message { get; } = message;

    public bool ShowList => Items.Count > 0;

    public IReadOnlyList<SaveChangesItem> Items { get; } = files
        .Select(path => new SaveChangesItem(Path.GetFileName(path), Path.GetDirectoryName(path) ?? ""))
        .ToList();

    public IReadOnlyList<string> Choices { get; } = choices;
}
