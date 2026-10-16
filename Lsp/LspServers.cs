using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AXamlIDE.Lsp;

    /// <summary>What the IDE currently has open: a root folder, and the .sln if one was opened.</summary>
    public sealed record LspWorkspace(string RootPath, string? SolutionPath);

    /// <summary>How to find, launch and prepare one language server. Add a new server by adding one of these.</summary>
    /// <param name="Id">Shown in the status bar; also the log prefix.</param>
    /// <param name="LanguageIds">The OpenDocument.LanguageId values this server handles. One server per language:
    /// if two configs claim the same language, the later one in the list wins.</param>
    /// <param name="ResolveCommand">Full path to the executable, or null if it isn't installed.</param>
    /// <param name="InstallHint">Shown when ResolveCommand returns null.</param>
    /// <param name="BuildArgs">Command-line arguments, which may depend on the workspace.</param>
    /// <param name="AfterInitialize">Runs once the initialize handshake is done - for servers that need
    /// a custom notification before they do anything (Roslyn's solution/open).</param>
    /// <param name="WaitsForProjectInitialization">The status reads "loading solution..." until the server
    /// sends workspace/projectInitializationComplete.</param>
    public sealed record LspServerConfig(
        string Id,
        IReadOnlyList<string> LanguageIds,
        Func<string?> ResolveCommand,
        string InstallHint,
        Func<LspWorkspace, IReadOnlyList<string>>? BuildArgs = null,
        Func<LspClient, LspWorkspace, Task>? AfterInitialize = null,
        bool WaitsForProjectInitialization = false);

    public static class LspServers
    {
        /// <summary>The language servers enabled by default.</summary>
        public static IReadOnlyList<LspServerConfig> Defaults => [Roslyn];

        // AXSG is temporarily disabled while the in-process completion engine is evaluated.
        private static LspServerConfig Axaml { get; } = new(
            Id: "axsg-lsp",
            LanguageIds: ["axaml"],
            ResolveCommand: () => FindTool("axsg-lsp", "AXAMLIDE_LSP"),
            InstallHint: "expected in ~/.dotnet/tools, or set AXAMLIDE_LSP");

        /// <summary>
        /// Microsoft's Roslyn language server (the engine behind the VS Code C# extension).
        ///   dotnet tool install -g roslyn-language-server --prerelease
        /// Check `roslyn-language-server --help`: if it exits straight away complaining about a required
        /// option (e.g. --extensionLogDirectory), add it to the arguments below. The reason shows in the
        /// status bar.
        /// </summary>
        private static LspServerConfig Roslyn { get; } = new(
            Id: "roslyn",
            LanguageIds: ["csharp"],
            ResolveCommand: () => FindTool("roslyn-language-server", "AXAMLIDE_ROSLYN"),
            InstallHint: "dotnet tool install -g roslyn-language-server --prerelease, or set AXAMLIDE_ROSLYN",
            BuildArgs: _ => ["--stdio", "--logLevel", "Information"],
            AfterInitialize: OpenRoslynWorkspace,
            WaitsForProjectInitialization: true);

        /// <summary>
        /// Community alternative that pushes diagnostics and needs no extra handshake:
        ///   dotnet tool install -g csharp-ls
        /// Check `csharp-ls --help` for the exact solution option on the version you install.
        /// </summary>
        public static LspServerConfig CSharpLs { get; } = new(
            Id: "csharp-ls",
            LanguageIds: ["csharp"],
            ResolveCommand: () => FindTool("csharp-ls", "AXAMLIDE_CSHARP_LS"),
            InstallHint: "dotnet tool install -g csharp-ls, or set AXAMLIDE_CSHARP_LS",
            BuildArgs: ws => ws.SolutionPath is { } sln
                ? new[] { "--solution", sln }
                : Array.Empty<string>());

        // Roslyn does nothing useful until it is told what to load.
        private static Task OpenRoslynWorkspace(LspClient client, LspWorkspace workspace)
        {
            var solution = workspace.SolutionPath ?? FindSolution(workspace.RootPath);
            if (solution is not null)
            {
                client.Notify("solution/open", new { solution = new Uri(solution).AbsoluteUri });
                return Task.CompletedTask;
            }

            // Plain folder with no solution: hand over the projects we can find instead.
            var projects = FindProjects(workspace.RootPath);
            if (projects.Length > 0)
                client.Notify("project/open", new { projects = projects.Select(p => new Uri(p).AbsoluteUri).ToArray() });

            return Task.CompletedTask;
        }

        private static string? FindSolution(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "*.sln").Concat(Directory.EnumerateFiles(root, "*.slnx"))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string[] FindProjects(string root)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var sep = Path.DirectorySeparatorChar;

            try
            {
                return
                [
                    .. Directory.EnumerateFiles(root, "*.csproj", options)
                              .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"))
                ];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        /// <summary>Environment override, then ~/.dotnet/tools, then PATH.</summary>
        private static string? FindTool(string name, string overrideEnvVar)
        {
            var overridePath = Environment.GetEnvironmentVariable(overrideEnvVar);
            if (!string.IsNullOrEmpty(overridePath))
                return File.Exists(overridePath) ? overridePath : null;

            var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;

            var toolsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            var inTools = Path.Combine(toolsDir, exe);
            if (File.Exists(inTools)) return inTools;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }
