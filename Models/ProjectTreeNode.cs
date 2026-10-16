using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AXamlIDE.Models;
    public enum NodeKind { Directory, File, Solution, Project, Folder, Placeholder }

    /// <summary>
    /// One node in either the plain filesystem tree (Open Folder) or the solution tree
    /// (Open Solution). Both share this type - only how each node's children get
    /// produced differs, via the populator delegate. Nodes with a populator get a
    /// throwaway "Loading..." child so the expander arrow shows before anything has been
    /// fetched; Populate/PopulateAsync swaps it for the real content, once.
    /// (FileTreeNode is no longer needed - delete it.)
    /// </summary>
    public class ProjectTreeNode
    {
        private static readonly ProjectTreeNode LoadingPlaceholder =
            new("Loading...", string.Empty, NodeKind.Placeholder, null);

        public string Name { get; }
        public string FullPath { get; }
        public NodeKind Kind { get; }
        public ObservableCollection<ProjectTreeNode> Children { get; } = new();
        public bool IsExpanded { get; set; }
        public string Icon => Kind switch
        {
            NodeKind.Directory => "M20 18H4V8h16m0-2h-8l-2-2H4c-1.11 0-2 .89-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2",
            NodeKind.Solution => "m17 18-5-2.18L7 18V5h10m0-2H7a2 2 0 0 0-2 2v16l7-3 7 3V5a2 2 0 0 0-2-2",
            NodeKind.Project => "M22 4a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h6l2 2zM2 6v14h18v2H2a2 2 0 0 1-2-2V6zm4 0v10h16V6z",
            NodeKind.Folder => "M20 18H4V8h16m0-2h-8l-2-2H4c-1.11 0-2 .89-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2",
            NodeKind.Placeholder => "M16 12a2 2 0 0 1 2-2 2 2 0 0 1 2 2 2 2 0 0 1-2 2 2 2 0 0 1-2-2m-6 0a2 2 0 0 1 2-2 2 2 0 0 1 2 2 2 2 0 0 1-2 2 2 2 0 0 1-2-2m-6 0a2 2 0 0 1 2-2 2 2 0 0 1 2 2 2 2 0 0 1-2 2 2 2 0 0 1-2-2",
            NodeKind.File when FullPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) => "M14 2H6a2 2 0 0 0-2 2v16c0 1.11.89 2 2 2h12c1.11 0 2-.89 2-2V8zm4 18H6V4h7v5h5zm-8.46-4.35 2.09 2.09L10.35 19 7 15.65l3.35-3.35 1.28 1.26zm7.46 0L13.65 19l-1.27-1.26 2.09-2.09-2.09-2.09 1.27-1.26z",
            NodeKind.File when FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "M5 3h2v2H5v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5h2v2H5c-1.07-.27-2-.9-2-2v-4a2 2 0 0 0-2-2H0v-2h1a2 2 0 0 0 2-2V5a2 2 0 0 1 2-2m14 0a2 2 0 0 1 2 2v4a2 2 0 0 0 2 2h1v2h-1a2 2 0 0 0-2 2v4a2 2 0 0 1-2 2h-2v-2h2v-5a2 2 0 0 1 2-2 2 2 0 0 1-2-2V5h-2V3zm-7 12a1 1 0 0 1 1 1 1 1 0 0 1-1 1 1 1 0 0 1-1-1 1 1 0 0 1 1-1m-4 0a1 1 0 0 1 1 1 1 1 0 0 1-1 1 1 1 0 0 1-1-1 1 1 0 0 1 1-1m8 0a1 1 0 0 1 1 1 1 1 0 0 1-1 1 1 1 0 0 1-1-1 1 1 0 0 1 1-1",
            NodeKind.File when FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) => "m11.5 15.97.41 2.44c-.26.14-.68.27-1.24.39-.57.13-1.24.2-2.01.2-2.21-.04-3.87-.7-4.98-1.96Q2 15.135 2 12.21c.05-2.31.72-4.08 2-5.32C5.32 5.64 6.96 5 8.94 5c.75 0 1.4.07 1.94.19s.94.25 1.2.4l-.58 2.49-1.06-.34c-.4-.1-.86-.15-1.39-.15-1.16-.01-2.12.36-2.87 1.1-.76.73-1.15 1.85-1.18 3.34 0 1.36.37 2.42 1.08 3.2.71.77 1.71 1.17 2.99 1.18l1.33-.12c.43-.08.79-.19 1.1-.32M13.89 19l.61-4H13l.34-2h1.5l.32-2h-1.5L14 9h1.5l.61-4h2l-.61 4h1l.61-4h2l-.61 4H22l-.34 2h-1.5l-.32 2h1.5L21 15h-1.5l-.61 4h-2l.61-4h-1l-.61 4zm2.95-6h1l.32-2h-1z",
            NodeKind.File => "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zm4 18H6V4h7v5h5z",
            _ => ""
        };

        private readonly Func<IEnumerable<ProjectTreeNode>>? _populator;
        private bool _populated;
        public bool NeedsPopulating => _populator is not null && !_populated;

        public ProjectTreeNode(string name, string fullPath, NodeKind kind, Func<IEnumerable<ProjectTreeNode>>? populator)
        {
            Name = name;
            FullPath = fullPath;
            Kind = kind;
            _populator = populator;

            if (populator is not null)
                Children.Add(LoadingPlaceholder);
        }

        /// <summary>Synchronous populate - fine for cheap populators (directories, the .sln parse).</summary>
        public void Populate()
        {
            if (_populated || _populator is null) return;
            _populated = true;

            Children.Clear();
            foreach (var child in _populator())
                Children.Add(child);
        }

        public void Reset()
        {
            _populated = false;
            Children.Clear();
            if (_populator is not null)
                Children.Add(LoadingPlaceholder);
        }

        /// <summary>
        /// Runs the populator on the thread pool (project evaluation goes through the MSBuild
        /// host and can take seconds), then fills Children back on the caller's (UI) context.
        /// </summary>
        public async Task PopulateAsync()
        {
            if (_populated || _populator is null) return;
            _populated = true;   // set first so a second Expanded event can't start another run

            try
            {
                var children = await Task.Run(() => _populator().ToList());

                Children.Clear();
                foreach (var child in children)
                    Children.Add(child);
            }
            catch (Exception ex)
            {
                Children.Clear();
                Children.Add(new ProjectTreeNode($"Failed: {ex.Message}", string.Empty, NodeKind.Placeholder, null));
            }
        }

        // ---- Filesystem tree (Open Folder) ----

        public static ProjectTreeNode CreateDirectory(string path)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return new ProjectTreeNode(string.IsNullOrEmpty(name) ? path : name, path, NodeKind.Directory,
                () => EnumerateDirectory(path));
        }

        private static IEnumerable<ProjectTreeNode> EnumerateDirectory(string path)
        {
            List<ProjectTreeNode> nodes;
            try
            {
                var dirs = Directory.EnumerateDirectories(path).OrderBy(d => d).Select(CreateDirectory);
                var files = Directory.EnumerateFiles(path).OrderBy(f => f)
                    .Select(f => new ProjectTreeNode(Path.GetFileName(f), f, NodeKind.File, null));
                nodes = [.. dirs, .. GroupCodeBehindFiles(files)];
            }
            catch (UnauthorizedAccessException)
            {
                // Folder we can't read - leave it empty rather than blow up the tree.
                nodes = new List<ProjectTreeNode>();
            }
            return nodes;
        }

        public static IEnumerable<ProjectTreeNode> GroupCodeBehindFiles(IEnumerable<ProjectTreeNode> nodes)
        {
            var items = nodes.ToList();
            var files = items
                .Where(node => node.Kind == NodeKind.File && !string.IsNullOrEmpty(node.FullPath))
                .ToList();
            var byPath = files.ToDictionary(node => node.FullPath, StringComparer.OrdinalIgnoreCase);
            var codeBehindPaths = files
                .Where(node => node.FullPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                .Select(node => node.FullPath + ".cs")
                .Where(byPath.ContainsKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var node in items)
            {
                if (node.Kind == NodeKind.File && codeBehindPaths.Contains(node.FullPath))
                    continue;

                if (node.Kind == NodeKind.File
                    && node.FullPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                    && byPath.TryGetValue(node.FullPath + ".cs", out var codeBehind))
                    node.Children.Add(codeBehind);

                yield return node;
            }
        }

        // ---- Solution tree (Open Solution) ----

        public static ProjectTreeNode CreateSolution(string solutionPath, IReadOnlyList<(string Name, string Path)> projects, MsBuildHostClient host)
            => new(Path.GetFileNameWithoutExtension(solutionPath), solutionPath, NodeKind.Solution,
                () => projects.Select(p => CreateProject(p.Name, p.Path, host)));

        private static ProjectTreeNode CreateProject(string name, string csprojPath, MsBuildHostClient host)
            => new(name, csprojPath, NodeKind.Project, () => SolutionLoader.LoadProjectItems(host, csprojPath));

        /// <summary>Folders under a project are fully known once the project has been evaluated
        /// (no further disk I/O), so this populates itself immediately.</summary>
        public static ProjectTreeNode CreateFolder(string name, IEnumerable<ProjectTreeNode> children)
        {
            var childList = children.ToList();
            var node = new ProjectTreeNode(name, string.Empty, NodeKind.Folder, () => childList);
            node.Populate();
            return node;
        }
    }
