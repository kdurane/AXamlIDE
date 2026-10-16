using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AXamlIDE.Models;

namespace AXamlIDE
{
    /// <summary>
    /// Parses a .sln file for its project list, then asks the isolated
    /// MSBuild host process to evaluate each .csproj — this class itself
    /// never references Microsoft.Build.* at all. See MSBuildHostClient for
    /// why that separation exists.
    /// </summary>
    public static class SolutionLoader
    {
        private static readonly Regex ProjectLineRegex = new(
            "^Project\\(\"\\{[0-9A-F-]+\\}\"\\)\\s*=\\s*\"([^\"]+)\",\\s*\"([^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ProjectTreeNode LoadSolution(string solutionPath, MsBuildHostClient host)
        {
            var solutionDir = Path.GetDirectoryName(solutionPath)!;
            var projects = new List<(string Name, string Path)>();
            var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var document = XDocument.Load(solutionPath);
                foreach (var project in document.Descendants().Where(element => element.Name.LocalName == "Project"))
                {
                    var projectReference = project.Attribute("Path")?.Value;
                    if (string.IsNullOrWhiteSpace(projectReference))
                        continue;

                    AddProject(Path.GetFileNameWithoutExtension(projectReference), projectReference);
                }
            }
            else
            {
                foreach (var line in File.ReadLines(solutionPath))
                {
                    var match = ProjectLineRegex.Match(line);
                    if (!match.Success) continue;

                    var name = match.Groups[1].Value;
                    var relativePath = match.Groups[2].Value;
                    AddProject(name, relativePath);
                }
            }

            void AddProject(string name, string relativePath)
            {
                if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    return;

                var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
                if (File.Exists(fullPath) && projectPaths.Add(fullPath))
                    projects.Add((name, fullPath));
            }

            for (var i = 0; i < projects.Count; i++)
            {
                string[] references;
                try
                {
                    references = host.GetProjectReferences(projects[i].Path);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    Debug.WriteLine($"[solution] Could not evaluate references in '{projects[i].Path}': {ex.Message}");
                    continue;
                }

                foreach (var referencePath in references)
                {
                    var fullPath = Path.GetFullPath(referencePath);
                    if (!fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(fullPath)
                        || !projectPaths.Add(fullPath))
                        continue;

                    projects.Add((Path.GetFileNameWithoutExtension(fullPath), fullPath));
                }
            }

            return ProjectTreeNode.CreateSolution(solutionPath, projects, host);
        }

        public static IEnumerable<ProjectTreeNode> LoadProjectItems(MsBuildHostClient host, string csprojPath)
        {
            var files = host.GetProjectFiles(csprojPath);
            var projectDir = Path.GetDirectoryName(csprojPath)!;
            return BuildFolderTree(projectDir, files);
        }

        /// <summary>Groups a flat file list into nested folders based on each file's
        /// path relative to the project directory. This mirrors the physical
        /// layout on disk rather than any "logical" grouping VS might apply for
        /// linked files living outside the project folder — a deliberate
        /// simplification for now.</summary>
        private static IEnumerable<ProjectTreeNode> BuildFolderTree(string projectDir, IEnumerable<string> files)
        {
            var root = new Dictionary<string, object>();

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(projectDir, file);
                var parts = relative.Split(Path.DirectorySeparatorChar);

                var current = root;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (current.TryGetValue(parts[i], out var next) && next is Dictionary<string, object> nextDict)
                    {
                        current = nextDict;
                    }
                    else
                    {
                        var newDict = new Dictionary<string, object>();
                        current[parts[i]] = newDict;
                        current = newDict;
                    }
                }

                current[parts[^1]] = file;
            }

            return ToNodes(root);
        }

        private static IEnumerable<ProjectTreeNode> ToNodes(Dictionary<string, object> level)
        {
            var ordered = level.OrderBy(kv => kv.Value is Dictionary<string, object> ? 0 : 1).ThenBy(kv => kv.Key);

            var nodes = ordered.Select(kv =>
            {
                var (name, value) = kv;
                return value switch
                {
                    Dictionary<string, object> children => ProjectTreeNode.CreateFolder(name, ToNodes(children)),
                    string path => new ProjectTreeNode(name, path, NodeKind.File, null),
                    _ => throw new InvalidOperationException("Unexpected node value.")
                };
            });

            return ProjectTreeNode.GroupCodeBehindFiles(nodes);
        }
    }
}