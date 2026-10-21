using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.CompletionEngine.AssemblyMetadata;
    public static class DepsJsonAssemblyListLoader
    {
        private record Library(string PackageName, string LibraryPath, string DllName);

        private static IEnumerable<Library> TransformDeps(JsonElement lstr)
        {
            foreach (var prop in lstr.EnumerateObject())
            {
                var package = prop.Name;
                if (!prop.Value.TryGetProperty("runtime", out var runtime)) continue;
                foreach (var dllprop in runtime.EnumerateObject())
                {
                    var libraryPath = dllprop.Name;
                    var dllName = libraryPath.Split('/').Last();
                    yield return new Library(package, libraryPath, dllName);
                }
            }
        }

        private static IEnumerable<string> GetNugetPackagesDirs()
        {
            // NUGET_PACKAGES moves the global packages folder, so it comes first
            var redirectedPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrEmpty(redirectedPath))
            {
                yield return redirectedPath;
            }

            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        public static IEnumerable<string> ParseFile(string path)
        {
            var dir = Path.GetDirectoryName(path);
            var nugetDirs = GetNugetPackagesDirs();
            using var deps = JsonDocument.Parse(File.ReadAllText(path));
            if (dir is null)
            {
                yield break;
            }

            var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
            if (target is null)
            {
                yield break;
            }

            foreach (var l in TransformDeps(deps.RootElement.GetProperty("targets").GetProperty(target)))
            {
                var localPath = Path.Combine(dir, l.DllName);
                if (File.Exists(localPath))
                {
                    yield return localPath;
                    continue;
                }
                foreach (var nugetPath in nugetDirs)
                {
                    foreach (var tolower in new[] { false, true })
                    {
                        var packagePath = Path.Combine(nugetPath,
                            tolower ? l.PackageName.ToLowerInvariant() : l.PackageName, l.LibraryPath);
                        if (!File.Exists(packagePath)) continue;
                        yield return packagePath;
                        break;
                    }
                }
            }
        }
    }
