using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// Reads and writes <see cref="WorkspaceState"/> as <c>.axamlide/workspace.json</c> in the workspace folder
/// (like .idea/ or .vs/). The folder is created when first needed and is never required: a missing,
/// unreadable or invalid file just means "nothing remembered", and a failed save is reported, not thrown.
/// </summary>
public sealed class WorkspaceStateStore
{
    public const string FolderName = ".axamlide";
    private const string FileName = "workspace.json";
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string StatePath(string workspaceFolder) => Path.Combine(workspaceFolder, FolderName, FileName);

    /// <summary>The remembered state, with paths made absolute. Empty when there is none (or it can't be read).</summary>
    public WorkspaceState Load(string workspaceFolder)
    {
        WorkspaceState? state;
        try
        {
            state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(StatePath(workspaceFolder)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WorkspaceState();
        }

        if (state is null || state.SchemaVersion != CurrentSchemaVersion)
            return new WorkspaceState();

        return state with
        {
            OpenFiles = [.. state.OpenFiles.Select(file => file with { Path = Absolute(workspaceFolder, file.Path) })],
            ActiveFile = state.ActiveFile is { } active ? Absolute(workspaceFolder, active) : null,
            StartupProject = state.StartupProject is { } startup ? Absolute(workspaceFolder, startup) : null
        };
    }

    /// <summary>Save the state (paths inside the workspace are stored relative). Returns an error message, or null.</summary>
    public string? Save(string workspaceFolder, WorkspaceState state)
    {
        var relative = state with
        {
            SchemaVersion = CurrentSchemaVersion,
            OpenFiles = [.. state.OpenFiles.Select(file => file with { Path = Relative(workspaceFolder, file.Path) })],
            ActiveFile = state.ActiveFile is { } active ? Relative(workspaceFolder, active) : null,
            StartupProject = state.StartupProject is { } startup ? Relative(workspaceFolder, startup) : null
        };

        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(workspaceFolder, FolderName));
            if (OperatingSystem.IsWindows())
                folder.Attributes |= FileAttributes.Hidden;   // dot-folders are already hidden elsewhere
            File.WriteAllText(StatePath(workspaceFolder), JsonSerializer.Serialize(relative, JsonOptions));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save the workspace state to '{StatePath(workspaceFolder)}': {ex.Message}";
        }
    }

    private static string Relative(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    private static string Absolute(string folder, string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(folder, path));
}
