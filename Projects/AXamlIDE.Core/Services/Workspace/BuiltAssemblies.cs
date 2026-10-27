using System;
using System.IO;
using System.Linq;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// Finds the DLL a build of a project wrote. Shared by XAML completion (it reads the project's types from it) and the
/// preview (the designer host loads it), so both pick the same file the same way.
/// </summary>
public static class BuiltAssemblies
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The newest <c>{assemblyName}.dll</c> under the evaluated output path or the project's bin folder, or null when
    /// there's none (not built yet). Reference assemblies (in a "ref" folder) are skipped: they have no code to run
    /// and only public types. This walks bin/ recursively, so callers that ask often should remember the answer.
    /// </summary>
    /// <param name="outputPath">The project's OutputPath (relative to its folder); bin/ is searched too, because
    /// the build may have been for another configuration or target framework.</param>
    /// <param name="accept">Further conditions on a candidate (the preview needs a runtimeconfig.json beside it).</param>
    public static string? FindNewest(string projectPath, string assemblyName, string? outputPath,
        Func<FileInfo, bool>? accept = null)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var outputDirectories = new[]
            {
                string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFullPath(outputPath, projectDirectory),
                Path.Combine(projectDirectory, "bin")
            }
            .Where(path => path is not null && Directory.Exists(path))
            .Distinct(PathComparer)
            .Cast<string>();

        return outputDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, assemblyName + ".dll", SearchOption.AllDirectories))
            .Distinct(PathComparer)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("ref", StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => accept?.Invoke(file) ?? true)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }
}
