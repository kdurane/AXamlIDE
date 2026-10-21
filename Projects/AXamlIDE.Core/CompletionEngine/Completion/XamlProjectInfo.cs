using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.Completion;

/// <summary>
/// What the project's other XAML files define, for hints the assemblies can't give: the x:Keys
/// ({StaticResource ...}) and the style classes its selectors use (Classes="...").
/// The document being edited is read on every completion, so it doesn't need to be in here.
/// </summary>
public sealed record XamlProjectInfo(IReadOnlyCollection<string> ResourceKeys, IReadOnlyCollection<string> StyleClasses)
{
    public static readonly XamlProjectInfo Empty = new([], []);
}
