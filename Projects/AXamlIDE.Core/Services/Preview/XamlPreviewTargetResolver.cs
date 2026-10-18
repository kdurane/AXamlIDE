using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AXamlIDE.Models;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Services.Preview;

/// <summary>
/// Works out how to preview an AXAML file: which built app hosts it (an executable project that
/// references the file's project and runs on desktop .NET), which assemblies to load, and which
/// Avalonia designer host to start. Browser/mobile heads are skipped: they can't host the designer.
/// Throws with a user-facing message when the file can't be previewed; the preview pane shows it.
/// </summary>
public sealed class XamlPreviewTargetResolver(MsBuildHostClient host)
{
    // Target-framework platforms whose builds can't run the desktop designer host.
    private static readonly string[] NonDesktopPlatforms = ["browser", "android", "ios", "maccatalyst", "tvos", "wasi"];

    public XamlPreviewTarget Resolve(string xamlProjectPath, string[] solutionProjects, string? workspaceRoot)
    {
        string? buildProblem = null;
        var skippedNonDesktop = false;

        foreach (var executableProject in FindHostProjects(xamlProjectPath, solutionProjects, workspaceRoot))
        {
            var executableOutputPath = host.GetProjectOutputPath(executableProject);
            if (!IsDesktopOutput(executableOutputPath))
            {
                skippedNonDesktop = true;
                continue;
            }

            string executableAssembly;
            try
            {
                executableAssembly = FindBuiltAssembly(
                    executableProject, host.GetProjectAssemblyName(executableProject),
                    requireRuntimeConfig: true, executableOutputPath);
            }
            catch (FileNotFoundException ex)
            {
                buildProblem ??= ex.Message;   // e.g. "Build 'X' first..."; another head may already be built
                continue;
            }

            if (GetRuntimeMajor(executableAssembly) is not { } runtimeMajor)
            {
                skippedNonDesktop = true;      // self-contained/non-desktop runtime config
                continue;
            }

            var xamlAssemblyName = host.GetProjectAssemblyName(xamlProjectPath);
            var xamlAssemblyInAppOutput = Path.Combine(
                Path.GetDirectoryName(executableAssembly)!, xamlAssemblyName + ".dll");
            var xamlAssembly = File.Exists(xamlAssemblyInAppOutput)
                ? xamlAssemblyInAppOutput
                : FindBuiltAssembly(
                    xamlProjectPath, xamlAssemblyName, requireRuntimeConfig: false,
                    host.GetProjectOutputPath(xamlProjectPath), runtimeMajor);
            var hostApp = FindDesignerHost(executableProject, runtimeMajor);

            return new XamlPreviewTarget(
                xamlAssembly,
                executableAssembly,
                hostApp,
                File.GetLastWriteTimeUtc(xamlAssembly),
                File.GetLastWriteTimeUtc(executableAssembly),
                File.GetLastWriteTimeUtc(hostApp));
        }

        if (buildProblem is not null)
            throw new InvalidOperationException(buildProblem);
        throw new InvalidOperationException(skippedNonDesktop
            ? $"only browser/mobile app projects reference '{Path.GetFileName(xamlProjectPath)}', and the designer needs a desktop app project."
            : $"no app project in the workspace references '{Path.GetFileName(xamlProjectPath)}'.");
    }

    /// <summary>Executable projects that reference the AXAML project: the solution's first, then any under the workspace root.</summary>
    private IEnumerable<string> FindHostProjects(string xamlProjectPath, string[] solutionProjects, string? workspaceRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> candidates = solutionProjects.Append(xamlProjectPath);
        if (workspaceRoot is not null && Directory.Exists(workspaceRoot))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            candidates = candidates.Concat(Directory.EnumerateFiles(workspaceRoot, "*.csproj", options)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                               && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
        }

        foreach (var candidate in candidates)
        {
            if (!seen.Add(Path.GetFullPath(candidate)))
                continue;
            if (IsExecutable(host.GetProjectOutputType(candidate))
                && ReferencesProject(candidate, xamlProjectPath, new(StringComparer.OrdinalIgnoreCase)))
                yield return candidate;
        }
    }

    /// <summary>False for outputs like bin/Debug/net10.0-browser/ or net10.0-android/.</summary>
    internal static bool IsDesktopOutput(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return true;   // unknown: let the build-output checks decide
        var framework = outputPath.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault(part =>
            part.StartsWith("net", StringComparison.OrdinalIgnoreCase));
        var dash = framework?.IndexOf('-') ?? -1;
        if (framework is null || dash < 0)
            return true;
        var platform = framework[(dash + 1)..];
        return !NonDesktopPlatforms.Any(p => platform.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private bool ReferencesProject(string projectPath, string targetProjectPath, HashSet<string> visited)
    {
        projectPath = Path.GetFullPath(projectPath);
        targetProjectPath = Path.GetFullPath(targetProjectPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(projectPath, targetProjectPath, comparison))
            return true;
        if (!visited.Add(projectPath))
            return false;

        foreach (var reference in host.GetProjectReferences(projectPath))
            if (ReferencesProject(reference, targetProjectPath, visited))
                return true;
        return false;
    }

    private static bool IsExecutable(string? outputType)
        => string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
           || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);

    private static string FindBuiltAssembly(
        string projectPath,
        string assemblyName,
        bool requireRuntimeConfig,
        string? evaluatedOutputPath,
        int? requiredRuntimeMajor = null)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var outputDirectories = new[]
        {
                  string.IsNullOrWhiteSpace(evaluatedOutputPath)
                      ? null
                      : Path.GetFullPath(evaluatedOutputPath, projectDirectory),
                  Path.Combine(projectDirectory, "bin")
              }.Where(path => path is not null && Directory.Exists(path))
            .Distinct(pathComparer)
            .Cast<string>()
            .ToArray();
        if (outputDirectories.Length <= 0)
            throw new FileNotFoundException(
                $"Build '{Path.GetFileName(projectPath)}' first to enable the AXAML preview.");
        {
            var assemblyFile = assemblyName + ".dll";
            var candidates = outputDirectories
                .SelectMany(directory => Directory.EnumerateFiles(directory, assemblyFile, SearchOption.AllDirectories))
                .Distinct(pathComparer)
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("ref", StringComparer.OrdinalIgnoreCase))
                .Where(path => !requireRuntimeConfig
                               || File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
                .Select(path => new FileInfo(path))
                .Where(file => requiredRuntimeMajor is null
                               || GetTargetFrameworkMajor(file.Directory?.Name) is not { } major
                               || major <= requiredRuntimeMajor)
                .OrderByDescending(file => file.LastWriteTimeUtc);

            if (candidates.FirstOrDefault() is { } target)
                return target.FullName;
        }

        throw new FileNotFoundException(
            $"Build '{Path.GetFileName(projectPath)}' first to enable the AXAML preview.");
    }

    private string FindDesignerHost(string projectPath, int runtimeMajor)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var assetsPath = host.GetProjectAssetsFile(projectPath);
        assetsPath = string.IsNullOrWhiteSpace(assetsPath) ? Path.Combine(projectDirectory, "obj", "project.assets.json") : Path.GetFullPath(assetsPath, projectDirectory);
        if (!File.Exists(assetsPath))
            throw new FileNotFoundException($"Restore '{Path.GetFileName(projectPath)}' before starting the preview.", assetsPath);

        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = assets.RootElement;
        var packageFolders = root.GetProperty("packageFolders")
            .EnumerateObject()
            .Select(folder => folder.Name)
            .ToArray();
        var avaloniaPackages = root.GetProperty("libraries")
            .EnumerateObject()
            .Where(library => library.Name.StartsWith("Avalonia/", StringComparison.OrdinalIgnoreCase)
                              && library.Value.TryGetProperty("path", out _))
            .Select(library => library.Value.GetProperty("path").GetString())
            .Where(path => !string.IsNullOrEmpty(path));

        var candidates = from packagePath in avaloniaPackages
                         from packageFolder in packageFolders
                         let toolsPath = Path.Combine(packageFolder, packagePath!, "tools")
                         where Directory.Exists(toolsPath)
                         from frameworkDirectory in Directory.EnumerateDirectories(toolsPath)
                         let major = GetTargetFrameworkMajor(Path.GetFileName(frameworkDirectory))
                         where major is { } frameworkMajor && frameworkMajor <= runtimeMajor
                         let hostPath = Path.Combine(frameworkDirectory, "designer", "Avalonia.Designer.HostApp.dll")
                         where File.Exists(hostPath)
                         orderby major descending
                         select hostPath;

        if (candidates.FirstOrDefault() is { } designerHost)
            return designerHost;

        throw new FileNotFoundException(
            $"No compatible Avalonia designer host was found for .NET {runtimeMajor} in '{Path.GetFileName(projectPath)}'. Restore an Avalonia project with DesignerSupport.");
    }

    /// <summary>
    /// The Microsoft.NETCore.App major version the app runs on, or null when its runtime config
    /// declares none (self-contained / browser builds), meaning the designer host can't run it.
    /// </summary>
    private static int? GetRuntimeMajor(string assemblyPath)
    {
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
        if (!runtimeConfig.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
            return null;
        if (runtimeOptions.TryGetProperty("framework", out var framework))
            return ParseRuntimeMajor(framework.GetProperty("version").GetString(), runtimeConfigPath);

        if (!runtimeOptions.TryGetProperty("frameworks", out var frameworks))
            return null;
        foreach (var candidate in frameworks.EnumerateArray())
            if (candidate.GetProperty("name").GetString() == "Microsoft.NETCore.App")
                return ParseRuntimeMajor(candidate.GetProperty("version").GetString(), runtimeConfigPath);

        return null;
    }

    private static int ParseRuntimeMajor(string? version, string runtimeConfigPath)
        => Version.TryParse(version, out var parsed)
            ? parsed.Major
            : throw new InvalidDataException($"Invalid runtime framework version '{version}' in '{runtimeConfigPath}'.");

    private static int? GetTargetFrameworkMajor(string? directoryName)
    {
        if (directoryName is null || !directoryName.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return null;
        var version = directoryName.AsSpan(3);
        var separator = version.IndexOfAny('-', '_');
        if (separator >= 0)
            version = version[..separator];
        return Version.TryParse(version, out var parsed) ? parsed.Major : null;
    }
}
