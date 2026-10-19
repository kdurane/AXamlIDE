using System.Linq;
using Avalonia.Data.Converters;
using AXamlIDE.Models;

namespace AXamlIDE.Views;

public static class PathConverters
{
    /// <summary>True when both bound paths are set and name the same file (the explorer's bold startup project).</summary>
    public static readonly IMultiValueConverter Same = new FuncMultiValueConverter<string?, bool>(paths =>
    {
        var list = paths.ToList();
        return list is [{ } a, { } b] && GitStatus.PathComparer.Equals(a, b);
    });
}
