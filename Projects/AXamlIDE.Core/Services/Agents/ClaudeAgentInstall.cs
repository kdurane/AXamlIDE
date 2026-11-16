using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Agents;

/// <summary>A release of the adapter to download: its version, the file for this computer, and its size.</summary>
public sealed record ClaudeAgentRelease(string Version, string AssetName, string DownloadUrl, long Size);

/// <summary>
/// Finds claude-agent-acp, the Claude Code ACP adapter (Apache-2.0, https://github.com/agentclientprotocol/claude-agent-acp),
/// and downloads it when the user asks. Its release files are single programs with Node built in, and the Claude
/// Code CLI (claude) sits beside them, so nothing else needs installing. Sign-in is the CLI's own (~/.claude),
/// shared with any other Claude Code on the computer.
///
/// Where it's looked for, in order:
///  1. AXAMLIDE_CLAUDE_ACP, the full path of the program (for a build of your own);
///  2. the IDE's own copy, in <see cref="InstallFolder"/> (what <see cref="InstallAsync"/> downloads);
///  3. claude-agent-acp on the PATH.
/// </summary>
public static class ClaudeAgentInstall
{
    private const string Repository = "agentclientprotocol/claude-agent-acp";
    private const string OverrideVariable = "AXAMLIDE_CLAUDE_ACP";

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>~/.local/share/AXamlIDE/agents/claude-agent-acp (on Windows, %LOCALAPPDATA%\AXamlIDE\...).</summary>
    public static string InstallFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "AXamlIDE", "agents", "claude-agent-acp");

    private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>The adapter's full path, or null when it isn't installed.</summary>
    public static string? FindAgent()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;

        var own = Path.Combine(InstallFolder, ExeName("claude-agent-acp"));
        return File.Exists(own) ? own : FindOnPath(ExeName("claude-agent-acp"));
    }

    /// <summary>The Claude Code CLI: the one shipped beside the adapter, else claude on the PATH. Null if neither.</summary>
    public static string? FindCli()
    {
        if (FindAgent() is { } agent && Path.Combine(Path.GetDirectoryName(agent)!, ExeName("claude")) is var beside && File.Exists(beside))
            return beside;
        return FindOnPath(ExeName("claude"));
    }

    /// <summary>The version the IDE downloaded last (from version.txt in <see cref="InstallFolder"/>), or null.</summary>
    public static string? InstalledVersion()
    {
        var file = Path.Combine(InstallFolder, "version.txt");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------- downloading

    /// <summary>
    /// The release file for a platform, as the project names them: claude-agent-acp-linux-x64.tar.gz,
    /// claude-agent-acp-linux-arm64-musl.tar.gz, claude-agent-acp-darwin-arm64.zip, claude-agent-acp-windows-x64.zip.
    /// Null for a platform it isn't built for.
    /// </summary>
    internal static string? AssetName(OSPlatform os, Architecture architecture, bool musl)
    {
        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        if (arch is null)
            return null;
        if (os == OSPlatform.Linux)
            return $"claude-agent-acp-linux-{arch}{(musl ? "-musl" : "")}.tar.gz";
        if (os == OSPlatform.OSX)
            return $"claude-agent-acp-darwin-{arch}.zip";
        if (os == OSPlatform.Windows)
            return $"claude-agent-acp-windows-{arch}.zip";
        return null;
    }

    /// <summary>The file this computer needs, or null if the adapter isn't built for it.</summary>
    private static string? AssetNameForThisComputer()
    {
        var os = OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OSPlatform.FreeBSD;
        var musl = OperatingSystem.IsLinux() && File.Exists("/etc/alpine-release");   // Alpine's C library
        return AssetName(os, RuntimeInformation.OSArchitecture, musl);
    }

    /// <summary>
    /// The newest release that has a file for this computer (asks GitHub, so it can fail with HttpRequestException).
    /// A release that has just been published has no files for a few minutes while they're built, so this looks
    /// through the last few rather than taking "latest".
    /// </summary>
    public static async Task<ClaudeAgentRelease> FindLatestReleaseAsync(CancellationToken ct = default)
    {
        var assetName = AssetNameForThisComputer()
            ?? throw new PlatformNotSupportedException($"claude-agent-acp has no download for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");

        await using var stream = await Http.GetStreamAsync($"https://api.github.com/repos/{Repository}/releases?per_page=10", ct);
        using var releases = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return NewestWith(releases.RootElement, assetName)
            ?? throw new InvalidOperationException($"No recent claude-agent-acp release has {assetName}.");
    }

    /// <summary>From GitHub's list of releases (newest first), the first finished one that has the file.</summary>
    internal static ClaudeAgentRelease? NewestWith(JsonElement releases, string assetName)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var release in releases.EnumerateArray())
        {
            var unfinished = release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True
                             || release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            if (!unfinished && ReleaseFrom(release, assetName) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Picks the file out of GitHub's release JSON (tag_name, assets[].name/size/browser_download_url).</summary>
    internal static ClaudeAgentRelease? ReleaseFrom(JsonElement release, string assetName)
    {
        if (AcpParser.String(release, "tag_name") is not { } tag || !release.TryGetProperty("assets", out var assets))
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (AcpParser.String(asset, "name") == assetName && AcpParser.String(asset, "browser_download_url") is { } url)
                return new ClaudeAgentRelease(tag.TrimStart('v'), assetName, url,
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0);
        }
        return null;
    }

    /// <summary>
    /// Download the release and unpack it into <see cref="InstallFolder"/>, replacing any copy already there.
    /// The old copy stays until the new one is fully unpacked, so a failed or cancelled download leaves it working.
    /// </summary>
    /// <param name="progress">Bytes downloaded so far.</param>
    public static async Task InstallAsync(ClaudeAgentRelease release, IProgress<long>? progress, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(InstallFolder)!;
        Directory.CreateDirectory(parent);
        var download = Path.Combine(parent, release.AssetName + ".download");
        var unpacked = InstallFolder + ".new";

        try
        {
            using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(download);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    progress?.Report(total);
                }
            }

            if (Directory.Exists(unpacked))
                Directory.Delete(unpacked, recursive: true);
            Directory.CreateDirectory(unpacked);
            await UnpackAsync(download, unpacked, ct);
            MakeRunnable(unpacked);
            await File.WriteAllTextAsync(Path.Combine(unpacked, "version.txt"), release.Version, ct);

            if (Directory.Exists(InstallFolder))
                Directory.Delete(InstallFolder, recursive: true);
            Directory.Move(unpacked, InstallFolder);
        }
        finally
        {
            File.Delete(download);
            if (Directory.Exists(unpacked))
                Directory.Delete(unpacked, recursive: true);
        }
    }

    private static async Task UnpackAsync(string archive, string folder, CancellationToken ct)
    {
        if (archive.EndsWith(".zip.download", StringComparison.Ordinal))
        {
            await ZipFile.ExtractToDirectoryAsync(archive, folder, ct);
            return;
        }
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, folder, overwriteFiles: true, ct);
    }

    /// <summary>A zip doesn't always keep the "executable" bit: set it on the two programs.</summary>
    private static void MakeRunnable(string folder)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var name in new[] { "claude-agent-acp", "claude" }.Select(name => Path.Combine(folder, name)).Where(File.Exists))
            File.SetUnixFileMode(name, File.GetUnixFileMode(name) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AXamlIDE");   // GitHub's API refuses requests without one
        return client;
    }
}
