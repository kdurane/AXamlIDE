using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Workspace;

/// <summary>A solution or folder that was opened: the start window's "Open recent" list.</summary>
public sealed record RecentWorkspace(string Path, DateTime LastOpened, bool Pinned = false)
{
    public bool IsSolution => IsSolutionPath(Path);

    /// <summary>The solution's name without its extension, or the folder's name.</summary>
    public string Name => IsSolution
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;

    public bool Exists => IsSolution ? File.Exists(Path) : Directory.Exists(Path);

    public static bool IsSolutionPath(string path)
        => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One heading of the recent list ("Pinned", "Today", "Yesterday"...) and what's under it.</summary>
public sealed record RecentWorkspaceGroup(string Title, IReadOnlyList<RecentWorkspace> Items);

/// <summary>
/// The recently opened solutions and folders, kept in recent.json beside settings.json (not per workspace, like
/// Visual Studio's MRU list). The list logic is static and pure so it can be tested; the store only reads and writes.
/// </summary>
public sealed class RecentWorkspaces(string? storePath = null)
{
    /// <summary>How many unpinned entries are kept (Visual Studio keeps a similar number).</summary>
    public const int MaxUnpinned = 30;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string StorePath { get; } = storePath ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AXamlIDE", "recent.json");

    /// <summary>The saved list, newest first; empty when there's none or it can't be read.</summary>
    public IReadOnlyList<RecentWorkspace> Load()
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<RecentWorkspace>>(File.ReadAllText(StorePath));
            return items is null ? [] : [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Path)).OrderByDescending(item => item.LastOpened)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Saves the list; false (nothing thrown) when it can't be written, which only loses the history.</summary>
    public bool Save(IReadOnlyList<RecentWorkspace> items)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(temp, StorePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------- the list

    /// <summary>
    /// <paramref name="path"/> was opened at <paramref name="now"/>: it moves to the top (keeping its pin), and the
    /// oldest unpinned entries beyond <see cref="MaxUnpinned"/> drop off.
    /// </summary>
    public static IReadOnlyList<RecentWorkspace> Opened(IReadOnlyList<RecentWorkspace> items, string path, DateTime now)
    {
        var full = System.IO.Path.GetFullPath(path);
        var pinned = items.FirstOrDefault(item => SamePath(item.Path, full))?.Pinned ?? false;
        var rest = items.Where(item => !SamePath(item.Path, full)).ToList();
        var kept = rest.Where(item => item.Pinned).Concat(rest.Where(item => !item.Pinned).Take(pinned ? MaxUnpinned : MaxUnpinned - 1));
        return [new RecentWorkspace(full, now, pinned), .. kept.OrderByDescending(item => item.LastOpened)];
    }

    public static IReadOnlyList<RecentWorkspace> Removed(IReadOnlyList<RecentWorkspace> items, string path)
        => [.. items.Where(item => !SamePath(item.Path, path))];

    public static IReadOnlyList<RecentWorkspace> PinToggled(IReadOnlyList<RecentWorkspace> items, string path)
        => [.. items.Select(item => SamePath(item.Path, path) ? item with { Pinned = !item.Pinned } : item)];

    /// <summary>
    /// The list as the start window shows it, Visual Studio style: pinned first, then by when each was last opened
    /// (Today, Yesterday, This week, This month, Older). Empty groups are left out; each group is newest first.
    /// </summary>
    public static IReadOnlyList<RecentWorkspaceGroup> Group(IReadOnlyList<RecentWorkspace> items, DateTime now)
    {
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // Monday
        var monthStart = new DateTime(today.Year, today.Month, 1);

        string TitleFor(RecentWorkspace item)
        {
            var day = item.LastOpened.Date;
            if (day >= today) return "Today";
            if (day == today.AddDays(-1)) return "Yesterday";
            if (day >= weekStart) return "This week";
            if (day >= monthStart) return "This month";
            return "Older";
        }

        var groups = new List<RecentWorkspaceGroup>();
        var pinned = items.Where(item => item.Pinned).OrderByDescending(item => item.LastOpened).ToList();
        if (pinned.Count > 0)
            groups.Add(new RecentWorkspaceGroup("Pinned", pinned));
        foreach (var title in new[] { "Today", "Yesterday", "This week", "This month", "Older" })
        {
            var under = items.Where(item => !item.Pinned && TitleFor(item) == title).OrderByDescending(item => item.LastOpened).ToList();
            if (under.Count > 0)
                groups.Add(new RecentWorkspaceGroup(title, under));
        }
        return groups;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
