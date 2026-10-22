using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

/// <summary>
/// Keeps a project file in step when a file or folder is moved, renamed or deleted, as Visual Studio does. Globs
/// pick up most files wherever they are; this fixes the paths the project names one by one:
///
///   item elements     Include / Update / Remove / Exclude:   &lt;AvaloniaResource Include="Assets/icon.ico"&gt;
///   properties        a value that is a path:                 &lt;ApplicationIcon&gt;Assets/icon.ico&lt;/ApplicationIcon&gt;
///
/// A folder carries the paths under it along ("Assets/a.png" follows "Assets"). Paths with wildcards or
/// $(Property) are left alone: they aren't about one file. A deleted file's item entries go; its properties stay,
/// so the build says what's missing rather than the setting quietly vanishing.
/// </summary>
static class ItemPaths
{
    /// <param name="newPath">Where it went, or null if it was deleted.</param>
    /// <returns>Whether the project file changed (and was saved).</returns>
    public static bool Move(string projectPath, string oldPath, string? newPath)
    {
        using var collection = new ProjectCollection();
        var root = ProjectRootElement.Open(projectPath, collection, preserveFormatting: true);
        var folder = Path.GetDirectoryName(projectPath)!;

        var from = Relative(folder, oldPath);
        if (from == ".")
            return false;   // the project's own folder

        // Moved out of the project's folder: for this project it's gone (unless it was outside already: a linked file).
        var to = newPath is null || IsInside(folder, oldPath) && !IsInside(folder, newPath)
            ? null
            : Relative(folder, newPath);

        foreach (var element in root.Items.ToList())
        {
            var include = Rewrite(element.Include, from, to);
            var update = Rewrite(element.Update, from, to);
            var remove = Rewrite(element.Remove, from, to);

            // Nothing left to name: the element goes (an Include of nothing would include nothing).
            if (element.Include.Length > 0 && include.Length == 0
                || element.Update.Length > 0 && update.Length == 0
                || element.Remove.Length > 0 && remove.Length == 0)
            {
                ItemProperties.Delete(element);
                continue;
            }

            if (include != element.Include)
                element.Include = include;
            if (update != element.Update)
                element.Update = update;
            if (remove != element.Remove)
                element.Remove = remove;
            var exclude = Rewrite(element.Exclude, from, to);
            if (exclude != element.Exclude)
                element.Exclude = exclude;
        }

        if (to is not null)
        {
            foreach (var property in root.Properties)
            {
                var value = Rewrite(property.Value, from, to);
                if (value != property.Value)
                    property.Value = value;
            }
        }

        if (!root.HasUnsavedChanges)
            return false;
        root.Save();
        return true;
    }

    /// <summary>
    /// A ";"-separated list of paths with <paramref name="from"/> (and anything under it) changed to
    /// <paramref name="to"/>, or dropped if that's null. Each path keeps its own style (backslashes, a trailing slash).
    /// </summary>
    private static string Rewrite(string list, string from, string? to)
    {
        if (list.Length == 0)
            return list;

        var parts = new List<string>();
        foreach (var part in list.Split(';'))
        {
            var path = part.Trim();
            if (path.Length == 0 || !IsPlainPath(path))
            {
                parts.Add(part);
                continue;
            }

            var normalized = ItemProperties.Normalize(path);
            var folderSlash = normalized.EndsWith('/');
            normalized = normalized.TrimEnd('/');

            string? replaced;
            if (string.Equals(normalized, from, ItemProperties.PathComparison))
                replaced = to;
            else if (normalized.StartsWith(from + "/", ItemProperties.PathComparison))
                replaced = to is null ? null : to + normalized[from.Length..];
            else
            {
                parts.Add(part);
                continue;
            }

            if (replaced is null)
                continue;   // deleted
            if (folderSlash)
                replaced += "/";
            if (path.Contains('\\'))
                replaced = replaced.Replace('/', '\\');
            parts.Add(part.Replace(path, replaced));   // keeps the whitespace around it
        }
        return string.Join(';', parts);
    }

    /// <summary>One file or folder: no wildcard, and no $(Property), @(Item) or %(Metadata) to expand.</summary>
    private static bool IsPlainPath(string path)
        => !path.Contains('*') && !path.Contains('?') && !path.Contains("$(") && !path.Contains("@(") && !path.Contains("%(");

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');

    private static bool IsInside(string folder, string path)
    {
        var relative = Relative(folder, path);
        return relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
