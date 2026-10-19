using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.Services.Running;

/// <summary>
/// Which projects can be a startup project: the toolbar lists the ones that build an executable
/// (OutputType Exe or WinExe), like Visual Studio's startup project box.
/// </summary>
public static class StartupProjectFinder
{
    public static bool IsRunnable(string? outputType)
        => string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
           || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The project files in an opened folder (no solution): *.csproj below it, skipping build output, hidden
    /// folders (.git, .axamlide...) and node_modules. Sorted by path.
    /// </summary>
    public static IReadOnlyList<string> FindProjectFiles(string folder, int maxDepth = 6)
    {
        var found = new List<string>();
        Walk(new DirectoryInfo(folder), 0);
        found.Sort(StringComparer.Ordinal);
        return found;

        void Walk(DirectoryInfo directory, int depth)
        {
            try
            {
                found.AddRange(directory.EnumerateFiles("*.csproj").Select(file => file.FullName));
                if (depth == maxDepth)
                    return;

                foreach (var child in directory.EnumerateDirectories())
                    if (!IsSkipped(child.Name))
                        Walk(child, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable folder: skip it
            }
        }
    }

    private static bool IsSkipped(string name)
        => name.StartsWith('.') || name is "bin" or "obj" or "node_modules";
}
