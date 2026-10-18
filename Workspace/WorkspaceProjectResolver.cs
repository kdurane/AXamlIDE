using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.Workspace;

public sealed class WorkspaceProjectResolver
{
    public string? ResolveSelectedProject(ProjectTreeNode? selection, IEnumerable<ProjectTreeNode> roots)
    {
        if (selection is null)
            return null;

        if ((selection.Kind == NodeKind.Project
             || Path.GetExtension(selection.FullPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            && File.Exists(selection.FullPath))
            return selection.FullPath;

        var rootNodes = roots.ToArray();
        foreach (var root in rootNodes)
        {
            var projectPath = FindSelectedProject(root, selection, currentProject: null);
            if (projectPath is not null)
                return projectPath;
        }

        var projects = rootNodes
            .SelectMany(DescendantsAndSelf)
            .Where(node => node.Kind == NodeKind.Project && File.Exists(node.FullPath))
            .Select(node => node.FullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (selection.Kind is NodeKind.Solution or NodeKind.Directory && projects.Length == 1)
            return projects[0];

        if (string.IsNullOrEmpty(selection.FullPath))
            return null;

        var directory = Directory.Exists(selection.FullPath)
            ? Path.GetFullPath(selection.FullPath)
            : Path.GetDirectoryName(selection.FullPath);
        var workspaceRoot = rootNodes.FirstOrDefault()?.FullPath;
        if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
            workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));

        while (!string.IsNullOrEmpty(directory))
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!string.IsNullOrEmpty(workspaceRoot) && !IsWithinDirectory(directory, workspaceRoot))
                break;

            var candidates = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 1)
                return candidates[0];
            if (candidates.Length > 1)
                return null;

            if (string.Equals(directory, workspaceRoot, StringComparison.Ordinal))
                break;
            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    public string? FindProjectForDocument(
        string documentPath,
        IEnumerable<string> solutionProjects,
        string? workspaceRoot)
    {
        var fullPath = Path.GetFullPath(documentPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var solutionProject = solutionProjects
            .Where(project =>
            {
                var directory = Path.GetDirectoryName(project);
                if (directory is null)
                    return false;
                var relative = Path.GetRelativePath(directory, fullPath);
                return !Path.IsPathRooted(relative)
                       && relative != ".."
                       && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison);
            })
            .OrderByDescending(project => Path.GetDirectoryName(project)?.Length ?? 0)
            .FirstOrDefault();

        if (solutionProject is not null)
            return solutionProject;

        var directoryPath = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directoryPath))
        {
            var candidates = Directory.EnumerateFiles(directoryPath, "*.csproj", SearchOption.TopDirectoryOnly)
                .Take(2)
                .ToArray();
            if (candidates.Length == 1)
                return candidates[0];
            if (candidates.Length > 1)
                return null;

            if (workspaceRoot is not null
                && string.Equals(Path.TrimEndingDirectorySeparator(directoryPath),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)), comparison))
                break;

            directoryPath = Path.GetDirectoryName(directoryPath);
        }

        return null;
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
               || (!Path.IsPathRooted(relative)
                   && relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison));
    }

    private static string? FindSelectedProject(
        ProjectTreeNode node,
        ProjectTreeNode selection,
        string? currentProject)
    {
        var project = node.Kind == NodeKind.Project ? node.FullPath : currentProject;
        if (ReferenceEquals(node, selection))
            return project;

        foreach (var child in node.Children)
        {
            var result = FindSelectedProject(child, selection, project);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static IEnumerable<ProjectTreeNode> DescendantsAndSelf(ProjectTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
    }


}
