using Avalonia;
using Avalonia.Data.Converters;
using AXamlIDE.Models;

namespace AXamlIDE.Views;

/// <summary>
/// How a <see cref="GitFileState"/> looks: its colour (the Git*Brush resources in Styles/ThemeResources.axaml,
/// which follow the theme) and its letter, as in VS Code (M, A, U, D, R, C). No state: the normal text colour, no letter.
/// </summary>
public static class GitStateConverters
{
    // Unchanged files get the tree's normal text brush explicitly: a binding can't fall back to the inherited
    // foreground (UnsetValue gives TextBlock's default, black).
    public static readonly IValueConverter Brush = new FuncValueConverter<GitFileState, object?>(state =>
        Application.Current?.TryGetResource(BrushKey(state), Application.Current.ActualThemeVariant, out var brush) == true
            ? brush
            : AvaloniaProperty.UnsetValue);

    public static readonly IValueConverter Letter = new FuncValueConverter<GitFileState, string>(state => state switch
    {
        GitFileState.Modified => "M",
        GitFileState.Added => "A",
        GitFileState.Untracked => "U",
        GitFileState.Deleted => "D",
        GitFileState.Renamed => "R",
        GitFileState.Conflicted => "C",
        _ => ""
    });

    private static string BrushKey(GitFileState state) => state switch
    {
        GitFileState.Modified => "GitModifiedBrush",
        GitFileState.Added => "GitAddedBrush",
        GitFileState.Untracked => "GitUntrackedBrush",
        GitFileState.Deleted => "GitDeletedBrush",
        GitFileState.Renamed => "GitRenamedBrush",
        GitFileState.Conflicted => "GitConflictedBrush",
        GitFileState.Ignored => "GitIgnoredBrush",
        _ => "TreeViewItemForeground"
    };
}
