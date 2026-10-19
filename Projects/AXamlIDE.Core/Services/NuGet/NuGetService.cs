using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.NuGet;

/// <summary>
/// Everything the NuGet manager needs, in one place: the user's sources and global packages folder (from the
/// dotnet CLI), searching / versions / details over the V3 feeds, a project's installed packages, and
/// installing / updating / uninstalling (dotnet add / remove package). Icons are cached for the session.
/// </summary>
public sealed class NuGetService : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "AXamlIDE" } }
    };
    private readonly ConcurrentDictionary<string, NuGetFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _icons = new(StringComparer.Ordinal);
    private Task<NuGetLocalPackages?>? _local;

    // ---------------------------------------------------------------- sources

    /// <summary>The enabled sources for a project folder (its nuget.config files, then the user's).</summary>
    public async Task<IReadOnlyList<NuGetSource>> GetSourcesAsync(string projectDirectory, CancellationToken ct)
        => [.. (await NuGetCli.ListSourcesAsync(projectDirectory, ct)).Where(source => source.IsEnabled)];

    private NuGetFeed? Feed(NuGetSource source)
        => source.IsLocalFolder ? null : _feeds.GetOrAdd(source.Url, _ => new NuGetFeed(_http, source));

    private Task<NuGetLocalPackages?> LocalAsync(string directory, CancellationToken ct)
    {
        if (_local is { IsFaulted: true } or { IsCanceled: true })
            _local = null;
        return _local ??= LoadLocalAsync(directory, ct);
    }

    private static async Task<NuGetLocalPackages?> LoadLocalAsync(string directory, CancellationToken ct)
        => await NuGetCli.GlobalPackagesFolderAsync(directory, ct) is { } folder && Directory.Exists(folder)
            ? new NuGetLocalPackages(folder)
            : null;

    // ---------------------------------------------------------------- browsing

    /// <summary>Search every given source; a package found on several shows once (the first source's).</summary>
    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        IReadOnlyList<NuGetSource> sources, string query, bool includePrerelease, int skip, int take, CancellationToken ct)
    {
        var feeds = sources.Select(Feed).OfType<NuGetFeed>().ToList();
        var results = await Task.WhenAll(feeds.Select(feed => feed.SearchAsync(query, includePrerelease, skip, take, ct)));
        return [.. results.SelectMany(list => list).DistinctBy(result => result.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every version on the given sources, newest first (prereleases only when asked for).</summary>
    public async Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(
        IReadOnlyList<NuGetSource> sources, string id, bool includePrerelease, CancellationToken ct)
    {
        var feeds = sources.Select(Feed).OfType<NuGetFeed>().ToList();
        var lists = await Task.WhenAll(feeds.Select(async feed =>
        {
            try { return await feed.GetVersionsAsync(id, ct); }
            catch (HttpRequestException) { return []; }   // one unreachable source shouldn't hide the others
        }));
        return [.. lists.SelectMany(list => list)
            .Select(NuGetVersion.TryParse).OfType<NuGetVersion>()
            .Where(version => includePrerelease || !version.IsPrerelease)
            .Distinct()
            .OrderDescending()];
    }

    /// <summary>The newest version on the sources (stable unless prereleases are included), or null.</summary>
    public async Task<NuGetVersion?> GetLatestAsync(IReadOnlyList<NuGetSource> sources, string id, bool includePrerelease, CancellationToken ct)
        => (await GetVersionsAsync(sources, id, includePrerelease, ct)).FirstOrDefault();

    /// <summary>
    /// One version's details: from the first source that has it, else from the global packages folder (offline,
    /// or a package from a source that can't be browsed). The local icon is used when there is one.
    /// </summary>
    public async Task<NuGetPackageDetails?> GetDetailsAsync(
        IReadOnlyList<NuGetSource> sources, string projectDirectory, string id, string version, CancellationToken ct)
    {
        var local = (await LocalAsync(projectDirectory, ct))?.TryGetDetails(id, version);
        foreach (var feed in sources.Select(Feed).OfType<NuGetFeed>())
        {
            try
            {
                if (await feed.GetDetailsAsync(id, version, ct) is { } remote)
                    return remote with { LocalIconPath = local?.LocalIconPath };
            }
            catch (HttpRequestException) when (local is not null)
            {
                return local;
            }
        }
        return local;
    }

    /// <summary>A package icon's bytes (a local file or a URL), cached; null when there's none or it can't be read.</summary>
    public Task<byte[]?> GetIconAsync(string? localPath, string? url)
    {
        var key = localPath ?? url;
        if (key is null)
            return Task.FromResult<byte[]?>(null);

        return _icons.GetOrAdd(key, async _ =>
        {
            try
            {
                if (localPath is not null)
                    return await File.ReadAllBytesAsync(localPath);
                return await _http.GetByteArrayAsync(url);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                           or TaskCanceledException or InvalidOperationException or UriFormatException)
            {
                return null;
            }
        });
    }

    /// <summary>A downloaded version's details from the global packages folder only (fast; for list rows).</summary>
    public async Task<NuGetPackageDetails?> GetLocalDetailsAsync(string projectDirectory, string id, string version, CancellationToken ct)
        => (await LocalAsync(projectDirectory, ct))?.TryGetDetails(id, version);

    /// <summary>The icon a downloaded version ships, if it's in the global packages folder.</summary>
    public async Task<string?> GetLocalIconPathAsync(string projectDirectory, string id, string version, CancellationToken ct)
        => (await LocalAsync(projectDirectory, ct))?.TryGetIconPath(id, version);

    // ---------------------------------------------------------------- the project

    public Task<InstalledPackages> GetInstalledAsync(string projectPath, CancellationToken ct) => NuGetCli.ListInstalledAsync(projectPath, ct);

    /// <summary>Install, or change the version of, a package (dotnet add package; restores).</summary>
    public Task<NuGetChangeResult> InstallAsync(string projectPath, string id, string version, CancellationToken ct)
        => NuGetCli.AddAsync(projectPath, id, version, ct);

    public Task<NuGetChangeResult> UninstallAsync(string projectPath, string id, CancellationToken ct)
        => NuGetCli.RemoveAsync(projectPath, id, ct);

    public void Dispose() => _http.Dispose();
}
