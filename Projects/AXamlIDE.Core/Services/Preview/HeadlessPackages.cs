using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.NuGet;

namespace AXamlIDE.Services.Preview;

/// <summary>What the ui_* tools' host needs beside the app: Avalonia.Headless, and folders holding what it uses that the app doesn't ship.</summary>
public sealed record HeadlessAssemblies(string AvaloniaVersion, string HeadlessPath, IReadOnlyList<string> ProbeFolders, IReadOnlyList<string> Downloaded);

/// <summary>
/// Finds Avalonia.Headless in the app's own Avalonia version (it must match: it builds on Avalonia's internals), and
/// the packages it depends on that the app's build doesn't have (Avalonia.Fonts.Inter, say). Looked for in the NuGet
/// global packages folder first, as any restore leaves them; else downloaded from nuget.org once into the IDE's own
/// package folder (<see cref="DownloadFolder"/>), the way a restore would. Only lib/ assemblies are taken.
/// </summary>
public static class HeadlessPackages
{
    private const string FeedUrl = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>In order of preference: the host runs on .NET 8 or later, so newer libs could need a newer runtime than the app's.</summary>
    private static readonly string[] Frameworks = ["net8.0", "net6.0", "netstandard2.1", "netstandard2.0"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// ~/.local/share/AXamlIDE.Packages on Linux (%LOCALAPPDATA%\AXamlIDE.Packages, ~/Library/Application Support/...):
    /// beside the IDE's folder, not in it, since publishing the IDE there clears it.
    /// </summary>
    public static string DownloadFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "AXamlIDE.Packages");

    public static async Task<HeadlessAssemblies> FindAsync(string depsJsonPath, CancellationToken ct)
    {
        var version = AvaloniaVersion(await File.ReadAllTextAsync(depsJsonPath, ct))
                      ?? throw new InvalidOperationException($"{Path.GetFileName(depsJsonPath)} doesn't list Avalonia: is this an Avalonia app?");
        var appFolder = Path.GetDirectoryName(depsJsonPath)!;

        var downloaded = new List<string>();
        var headless = await PackageAsync("Avalonia.Headless", version, downloaded, ct);
        var probe = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Avalonia", "Avalonia.Headless" };
        var pending = new Queue<string>(Dependencies(headless.Folder));
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id) || File.Exists(Path.Combine(appFolder, id + ".dll")))
                continue;
            var package = await PackageAsync(id, version, downloaded, ct);
            probe.Add(package.Lib);
            foreach (var next in Dependencies(package.Folder))
                pending.Enqueue(next);
        }

        var headlessPath = Path.Combine(headless.Lib, "Avalonia.Headless.dll");
        if (!File.Exists(headlessPath))
            throw new InvalidOperationException($"Avalonia.Headless {version} has no Avalonia.Headless.dll for .NET 8 or .NET Standard.");
        return new HeadlessAssemblies(version, headlessPath, probe, downloaded);
    }

    /// <summary>The Avalonia version a deps.json was built with ("Avalonia/12.2.0" in its libraries), or null.</summary>
    public static string? AvaloniaVersion(string depsJson)
    {
        using var document = JsonDocument.Parse(depsJson);
        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
            return null;
        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Name.StartsWith("Avalonia/", StringComparison.OrdinalIgnoreCase))
                return library.Name["Avalonia/".Length..];
        }
        return null;
    }

    /// <summary>The ids a package depends on, from any of its framework groups (they list the same Avalonia packages).</summary>
    private static IEnumerable<string> Dependencies(string packageFolder)
    {
        var id = Path.GetFileName(Path.GetDirectoryName(packageFolder))!;
        var version = Path.GetFileName(packageFolder);
        var details = new NuGetLocalPackages(Path.GetDirectoryName(Path.GetDirectoryName(packageFolder))!).TryGetDetails(id, version);
        return details?.DependencyGroups.SelectMany(g => g.Dependencies).Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase) ?? [];
    }

    private sealed record Package(string Folder, string Lib);

    private static async Task<Package> PackageAsync(string id, string version, List<string> downloaded, CancellationToken ct)
    {
        foreach (var root in PackageFolders())
        {
            var folder = Path.Combine(root, id.ToLowerInvariant(), version.ToLowerInvariant());
            if (LibOf(folder) is { } lib)
                return new Package(folder, lib);
        }

        var target = Path.Combine(DownloadFolder, id.ToLowerInvariant(), version.ToLowerInvariant());
        await DownloadAsync(id, version, target, ct);
        downloaded.Add($"{id} {version}");
        return new Package(target, LibOf(target) ?? throw new InvalidOperationException($"{id} {version} has no lib for .NET 8 or .NET Standard."));
    }

    /// <summary>The global packages folder (NUGET_PACKAGES moves it), then the IDE's own.</summary>
    private static IEnumerable<string> PackageFolders()
    {
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } redirected)
            yield return redirected;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        yield return DownloadFolder;
    }

    private static string? LibOf(string packageFolder)
        => Frameworks.Select(f => Path.Combine(packageFolder, "lib", f)).FirstOrDefault(Directory.Exists);

    /// <summary>The .nupkg's lib/ folder and .nuspec, into <paramref name="target"/> (unpacked beside it first, so a failed download leaves nothing).</summary>
    private static async Task DownloadAsync(string id, string version, string target, CancellationToken ct)
    {
        var (lowerId, lowerVersion) = (id.ToLowerInvariant(), version.ToLowerInvariant());
        var url = $"{FeedUrl}/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";
        byte[] bytes;
        try
        {
            bytes = await Http.GetByteArrayAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't download {id} {version} from nuget.org: {ex.Message}", ex);
        }

        var staging = target + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var wanted = entry.FullName.StartsWith("lib/", StringComparison.Ordinal)
                                 || entry.FullName.Equals(lowerId + ".nuspec", StringComparison.OrdinalIgnoreCase);
                    if (!wanted || entry.FullName.EndsWith('/'))
                        continue;
                    var path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        continue;   // a path that climbs out of the folder
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(Entry(path, entry, lowerId), overwrite: true);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }

        // The nuspec as the global packages folder names it (lower case), so TryGetDetails finds it.
        static string Entry(string path, ZipArchiveEntry entry, string lowerId)
            => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Path.GetDirectoryName(path)!, lowerId + ".nuspec")
                : path;
    }
}
