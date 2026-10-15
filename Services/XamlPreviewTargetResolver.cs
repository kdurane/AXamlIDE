using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AXaxmlIDE.Services;

public sealed class XamlPreviewTargetResolver(MsBuildHostClient host)
{
    public XamlPreviewTarget Resolve(string xamlProjectPath, string[] solutionProjects, string? workspaceRoot)
    {
        var candidateProjects = solutionProjects
            .Append(xamlProjectPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var executableProject = candidateProjects.FirstOrDefault(project =>
            IsExecutable(host.GetProjectOutputType(project))
            && ReferencesProject(project, xamlProjectPath, new(StringComparer.OrdinalIgnoreCase)));

        if (executableProject is null && workspaceRoot is not null && Directory.Exists(workspaceRoot))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var candidate in Directory.EnumerateFiles(workspaceRoot, "*.csproj", options)
                         .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                if (!IsExecutable(host.GetProjectOutputType(candidate))
                    || !ReferencesProject(candidate, xamlProjectPath,
                        new(StringComparer.OrdinalIgnoreCase))) continue;
                executableProject = candidate;
                break;
            }
        }

        if (executableProject is null)
            throw new InvalidOperationException(
                $"No executable project in the workspace references '{Path.GetFileName(xamlProjectPath)}'.");

        var executableName = host.GetProjectAssemblyName(executableProject);
        var executableOutputPath = host.GetProjectOutputPath(executableProject);
        var executableAssembly = FindBuiltAssembly(
            executableProject, executableName, requireRuntimeConfig: true, executableOutputPath);
        var runtimeMajor = GetRuntimeMajor(executableAssembly);
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

    private bool ReferencesProject(string projectPath, string targetProjectPath, System.Collections.Generic.HashSet<string> visited)
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

    private static int GetRuntimeMajor(string assemblyPath)
    {
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
        var runtimeOptions = runtimeConfig.RootElement.GetProperty("runtimeOptions");
        if (runtimeOptions.TryGetProperty("framework", out var framework))
            return ParseRuntimeMajor(framework.GetProperty("version").GetString(), runtimeConfigPath);

        if (!runtimeOptions.TryGetProperty("frameworks", out var frameworks))
            throw new InvalidDataException($"No Microsoft.NETCore.App framework declaration in '{runtimeConfigPath}'.");
        foreach (var candidate in frameworks.EnumerateArray())
            if (candidate.GetProperty("name").GetString() == "Microsoft.NETCore.App")
                return ParseRuntimeMajor(candidate.GetProperty("version").GetString(), runtimeConfigPath);

        throw new InvalidDataException($"No Microsoft.NETCore.App framework declaration in '{runtimeConfigPath}'.");
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
