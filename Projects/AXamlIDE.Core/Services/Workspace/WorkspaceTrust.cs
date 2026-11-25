using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// Rider's "Trust and Open Project": the folders whose code may run. Opening a solution runs code from it before
/// anything is clicked (MSBuild evaluates its project files, the language server loads its analyzers and source
/// generators, previews load its assemblies), so a solution or folder that isn't under a trusted folder is asked about
/// first (MainWindowViewModel.OpenWorkspaceAsync). Trusting a folder trusts everything under it.
///
/// Kept in trusted.json beside settings.json, as a list of folder paths. When there's no file yet, it starts with the
/// folders of the recent list (see <see cref="StartWith"/>), so what was already being opened isn't asked about.
/// The list logic is static and pure so it can be tested; the store only reads and writes.
/// </summary>
public sealed class WorkspaceTrust(string? storePath = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly StringComparison PathComparison
        = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private IReadOnlyList<string>? _folders;

    public string StorePath { get; } = storePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AXamlIDE", "trusted.json");

    /// <summary>The trusted folders (full paths); empty when there's no file or it can't be read.</summary>
    public IReadOnlyList<string> Folders => _folders ??= Load();

    /// <summary>Whether <paramref name="workspacePath"/> (a solution file or a folder) is under a trusted folder.</summary>
    public bool IsTrusted(string workspacePath) => IsTrusted(Folders, FolderOf(workspacePath));

    /// <summary>Trust <paramref name="folder"/> and everything under it; false (nothing thrown) if it couldn't be saved.</summary>
    public bool Trust(string folder) => Save(Trusted(Folders, folder));

    /// <summary>
    /// The first time (no trusted.json yet): trust the folders of <paramref name="workspacePaths"/> (the recent list),
    /// so updating to a version with trust doesn't ask about every solution already in use.
    /// </summary>
    public void StartWith(IEnumerable<string> workspacePaths)
    {
        if (File.Exists(StorePath))
            return;
        Save(workspacePaths.Aggregate((IReadOnlyList<string>)[], (folders, path) => Trusted(folders, FolderOf(path))));
    }

    private IReadOnlyList<string> Load()
    {
        try
        {
            var folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath));
            return folders is null ? [] : [.. folders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Select(Normalize)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private bool Save(IReadOnlyList<string> folders)
    {
        _folders = folders;   // trusted for this run even if it can't be written
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(folders, JsonOptions));
            File.Move(temp, StorePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------- the list

    /// <summary>The folder a workspace is in: a solution's folder, or the folder itself.</summary>
    public static string FolderOf(string workspacePath)
        => Normalize(RecentWorkspace.IsSolutionPath(workspacePath) ? Path.GetDirectoryName(Path.GetFullPath(workspacePath))! : workspacePath);

    /// <summary>Whether <paramref name="folder"/> is one of <paramref name="trusted"/> or under one.</summary>
    public static bool IsTrusted(IReadOnlyList<string> trusted, string folder)
    {
        var full = Normalize(folder);
        return trusted.Any(root => IsSameOrUnder(full, root));
    }

    /// <summary><paramref name="trusted"/> with <paramref name="folder"/> added (folders under it are dropped, as it covers them).</summary>
    public static IReadOnlyList<string> Trusted(IReadOnlyList<string> trusted, string folder)
    {
        var full = Normalize(folder);
        if (IsTrusted(trusted, full))
            return trusted;
        return [.. trusted.Where(root => !IsSameOrUnder(root, full)), full];
    }

    private static bool IsSameOrUnder(string path, string root)
        => string.Equals(path, root, PathComparison)
           || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>The full path without a trailing separator (except a root such as "/" or "C:\").</summary>
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(Path.VolumeSeparatorChar) ? Path.GetPathRoot(full)! : trimmed;
    }
}
