using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.NuGet;

/// <summary>
/// One NuGet V3 feed over HTTP (https://learn.microsoft.com/nuget/api/overview): its service index tells where
/// search, the version list ("flat container") and the registration metadata are. Read-only; installing goes
/// through the dotnet CLI. Feeds that need signing in aren't supported here (dotnet still restores from them).
/// </summary>
public sealed class NuGetFeed(HttpClient http, NuGetSource source)
{
    private Task<ServiceIndex>? _index;

    public NuGetSource Source => source;

    private sealed record ServiceIndex(string? Search, string? PackageBase, string? Registrations);

    /// <summary>Search the feed (Browse). An empty query lists the most popular packages, as nuget.org does.</summary>
    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(string query, bool includePrerelease, int skip, int take, CancellationToken ct)
    {
        var index = await IndexAsync(ct);
        if (index.Search is null)
            return [];

        var url = $"{index.Search}?q={Uri.EscapeDataString(query)}&skip={skip}&take={take}&prerelease={(includePrerelease ? "true" : "false")}&semVerLevel=2.0.0";
        using var json = await GetJsonAsync(url, ct);
        if (json is null || !json.RootElement.TryGetProperty("data", out var data))
            return [];

        return [.. data.EnumerateArray().Select(item => new NuGetSearchResult(
            Str(item, "id") ?? "",
            Str(item, "version") ?? "",
            Str(item, "description"),
            Authors(item),
            item.TryGetProperty("totalDownloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : null,
            Str(item, "iconUrl"),
            item.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True,
            source.Name))];
    }

    /// <summary>Every version of a package on the feed (listed or not), oldest first; empty if it isn't there.</summary>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct)
    {
        var index = await IndexAsync(ct);
        if (index.PackageBase is null)
            return [];

        using var json = await GetJsonAsync($"{index.PackageBase}{id.ToLowerInvariant()}/index.json", ct);
        return json is not null && json.RootElement.TryGetProperty("versions", out var versions)
            ? [.. versions.EnumerateArray().Select(version => version.GetString()).OfType<string>()]
            : [];
    }

    /// <summary>One version's details from the registration (the catalog entry), or null if the feed hasn't it.</summary>
    public async Task<NuGetPackageDetails?> GetDetailsAsync(string id, string version, CancellationToken ct)
    {
        var index = await IndexAsync(ct);
        if (index.Registrations is null || NuGetVersion.TryParse(version) is not { } wanted)
            return null;

        using var registration = await GetJsonAsync($"{index.Registrations}{id.ToLowerInvariant()}/index.json", ct);
        if (registration is null || !registration.RootElement.TryGetProperty("items", out var pages))
            return null;

        foreach (var page in pages.EnumerateArray())
        {
            // Big packages page their versions out: only fetch the page whose range holds the version.
            if (NuGetVersion.TryParse(Str(page, "lower")) is { } lower && wanted < lower
                || NuGetVersion.TryParse(Str(page, "upper")) is { } upper && wanted > upper)
                continue;

            JsonDocument? fetched = null;
            try
            {
                var leaves = page;
                if (!page.TryGetProperty("items", out _) && Str(page, "@id") is { } pageUrl)
                {
                    fetched = await GetJsonAsync(pageUrl, ct);
                    if (fetched is null)
                        continue;
                    leaves = fetched.RootElement;
                }

                if (!leaves.TryGetProperty("items", out var items))
                    continue;
                foreach (var leaf in items.EnumerateArray())
                {
                    if (!leaf.TryGetProperty("catalogEntry", out var entry))
                        continue;
                    if (entry.ValueKind == JsonValueKind.String)
                        continue;   // a link instead of inline data: not seen on V3 feeds in practice
                    if (NuGetVersion.TryParse(Str(entry, "version")) is { } entryVersion && entryVersion.Equals(wanted))
                        return ReadCatalogEntry(entry);
                }
            }
            finally
            {
                fetched?.Dispose();
            }
        }

        return null;
    }

    private static NuGetPackageDetails ReadCatalogEntry(JsonElement entry)
    {
        var groups = new List<NuGetDependencyGroup>();
        if (entry.TryGetProperty("dependencyGroups", out var dependencyGroups) && dependencyGroups.ValueKind == JsonValueKind.Array)
            foreach (var group in dependencyGroups.EnumerateArray())
            {
                var dependencies = group.TryGetProperty("dependencies", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(d => new NuGetDependency(Str(d, "id") ?? "", Str(d, "range"))).ToList()
                    : [];
                groups.Add(new NuGetDependencyGroup(Str(group, "targetFramework") ?? "Any", dependencies));
            }

        string? deprecation = null;
        if (entry.TryGetProperty("deprecation", out var deprecated) && deprecated.ValueKind == JsonValueKind.Object)
        {
            var reasons = deprecated.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array
                ? string.Join(", ", r.EnumerateArray().Select(reason => reason.GetString()))
                : "deprecated";
            var alternate = deprecated.TryGetProperty("alternatePackage", out var alt) ? Str(alt, "id") : null;
            deprecation = $"Deprecated ({reasons}). {Str(deprecated, "message")}{(alternate is null ? "" : $" Use {alternate} instead.")}".Trim();
        }

        var vulnerabilities = entry.TryGetProperty("vulnerabilities", out var vulns) && vulns.ValueKind == JsonValueKind.Array
            ? vulns.EnumerateArray().Select(v => $"{SeverityName(Str(v, "severity"))}: {Str(v, "advisoryUrl")}").ToList()
            : [];

        return new NuGetPackageDetails(
            Str(entry, "id") ?? "",
            Str(entry, "version") ?? "",
            Str(entry, "title"),
            Str(entry, "description"),
            Authors(entry),
            Str(entry, "licenseExpression"),
            Str(entry, "licenseUrl"),
            Str(entry, "projectUrl"),
            DateTimeOffset.TryParse(Str(entry, "published"), out var published) && published.Year > 1900 ? published : null,
            entry.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? [.. tags.EnumerateArray().Select(tag => tag.GetString()).OfType<string>()]
                : [],
            groups,
            deprecation,
            vulnerabilities,
            Str(entry, "iconUrl"),
            LocalIconPath: null);
    }

    private static string SeverityName(string? severity) => severity switch
    {
        "0" => "Low", "1" => "Moderate", "2" => "High", "3" => "Critical", _ => "Vulnerability"
    };

    // ---------------------------------------------------------------- plumbing

    private Task<ServiceIndex> IndexAsync(CancellationToken ct)
    {
        if (_index is { IsFaulted: true } or { IsCanceled: true })
            _index = null;   // offline or cancelled last time: ask again
        return _index ??= LoadIndexAsync(ct);
    }

    private async Task<ServiceIndex> LoadIndexAsync(CancellationToken ct)
    {
        using var json = await GetJsonAsync(source.Url, ct);
        if (json is null || !json.RootElement.TryGetProperty("resources", out var resources))
        {
            _index = null;   // try again next time
            return new ServiceIndex(null, null, null);
        }

        string? Find(params string[] types)
        {
            foreach (var type in types)
                foreach (var resource in resources.EnumerateArray())
                    if (resource.TryGetProperty("@type", out var t) && t.GetString() == type)
                        return Str(resource, "@id");
            return null;
        }

        static string? Slash(string? url) => url is null || url.EndsWith('/') ? url : url + "/";

        return new ServiceIndex(
            Find("SearchQueryService/3.5.0", "SearchQueryService/3.0.0-rc", "SearchQueryService"),
            Slash(Find("PackageBaseAddress/3.0.0")),
            Slash(Find("RegistrationsBaseUrl/3.6.0", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl")));
    }

    /// <summary>GET and parse; null for 404 (not on this feed). Other failures throw (the caller shows them).</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new HttpRequestException($"{source.Name} needs signing in; browsing it isn't supported here (restore and install still work).");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>"authors" is a string in some responses and an array in others.</summary>
    private static string? Authors(JsonElement element)
        => element.TryGetProperty("authors", out var authors)
            ? authors.ValueKind switch
            {
                JsonValueKind.String => authors.GetString(),
                JsonValueKind.Array => string.Join(", ", authors.EnumerateArray().Select(a => a.GetString())),
                _ => null
            }
            : null;
}
