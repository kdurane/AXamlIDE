using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AXamlIDE.Models;
using AXamlIDE.MsBuildHost;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// Long-lived helper process that evaluates projects with the system SDK's MSBuild.
/// Synchronous by design (one request in flight at a time); call it from Task.Run
/// when you don't want to block the UI - ProjectTreeNode.PopulateAsync does.
/// </summary>
public sealed class MsBuildHostClient : IDisposable
{
    private readonly string _hostPath;
    private readonly object _lock = new();
    private Process? _process;

    private readonly ConcurrentDictionary<(string Path, string Configuration), CachedEvaluation> _evaluations = new();

    public MsBuildHostClient(string hostExecutablePath)
    {
        if (!File.Exists(hostExecutablePath))
            throw new FileNotFoundException(
                "MSBuild host executable not found - build AXamlIDE.msbuildhost first.", hostExecutablePath);

        _hostPath = hostExecutablePath;
        Start();
    }

    private void Start()
    {
        var utf8 = new UTF8Encoding(false);   // no BOM on the pipe

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(_hostPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // Drain stderr so a chatty host can never fill the pipe and hang a request.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Debug.WriteLine("[msbuildhost] " + e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();
        _process = process;
    }

    private void Restart()
    {
        Kill();
        Start();
    }

    /// <summary>Everything the Solution Explorer shows under the project, from one evaluation.</summary>
    public ProjectContents GetProjectContents(string projectPath)
    {
        var data = GetProjectData(projectPath);
        var dependencies = (data.Dependencies ?? [])
            .Select(d => new ProjectDependency(Enum.Parse<DependencyKind>(d.Kind), d.Name, d.Version, d.Path, d.IsImplicit))
            .ToArray();
        return new ProjectContents(data.Files, data.Folders ?? [], dependencies, data.ProjectAssetsFile);
    }

    /// <summary>The project's properties (assembly name, output path...), all from one evaluation.</summary>
    /// <param name="configuration">"Debug", "Release"...; null for the project's default (Debug). Only OutputPath
    /// usually differs between them.</param>
    public ProjectFacts GetProjectFacts(string projectPath, string? configuration = null)
    {
        var data = GetProjectData(projectPath, configuration);
        return new ProjectFacts(
            data.AssemblyName
                ?? throw new InvalidOperationException($"MSBuild did not provide an assembly name for '{projectPath}'."),
            data.OutputType,
            data.OutputPath?.Replace('\\', Path.DirectorySeparatorChar),
            data.ProjectAssetsFile,
            data.ProjectReferences,
            data.TargetFrameworks ?? []);
    }

    /// <summary>
    /// Forget every remembered evaluation, so the next question evaluates again. For an explicit refresh: the
    /// cache can't see a Directory.Build.props that was newly created above a project (it only watches the files
    /// the project already imported).
    /// </summary>
    public void ClearCache() => _evaluations.Clear();

    // ---- one file's build action and metadata (the Properties panel)

    public ProjectItemProperties GetItemProperties(string projectPath, string filePath)
        => ToProperties(Send(new HostRequest(projectPath, HostCommands.GetItem, filePath)));

    /// <summary>Change the file's build action (item type); writes the project file.</summary>
    public ProjectItemProperties SetItemType(string projectPath, string filePath, string itemType)
        => ToProperties(SendAndForget(new HostRequest(projectPath, HostCommands.SetItem, filePath, ItemType: itemType)));

    /// <summary>Change one metadata value of the file ("" clears it); writes the project file.</summary>
    public ProjectItemProperties SetItemMetadata(string projectPath, string filePath, string name, string value)
        => ToProperties(SendAndForget(new HostRequest(projectPath, HostCommands.SetItem, filePath, MetadataName: name, MetadataValue: value)));

    /// <summary>
    /// A file or folder was moved or renamed (<paramref name="newPath"/>), or deleted (null): rewrite or drop the
    /// project file's explicit entries for it. Returns whether the project file changed.
    /// </summary>
    public bool MoveItem(string projectPath, string oldPath, string? newPath)
        => SendAndForget(new HostRequest(projectPath, HostCommands.MoveItem, oldPath, NewPath: newPath)).ProjectChanged;

    private static ProjectItemProperties ToProperties(HostResponse response)
        => response.Item is { } item
            ? new ProjectItemProperties(item.ItemType, item.AvailableItemTypes, item.Metadata)
            : throw new InvalidOperationException("MSBuild host returned no item.");

    /// <summary>
    /// The project's evaluation, remembered per project and configuration. Opening a solution, expanding it in the
    /// explorer, listing startup projects, completion and the preview all ask about the same projects, and each
    /// evaluation takes a while (and holds up every other question to the host), so one is shared until the project
    /// file or one of its local imports (<see cref="HostResponse.Imports"/>) is written.
    /// </summary>
    private HostResponse GetProjectData(string projectPath, string? configuration = null)
    {
        var key = (Path.GetFullPath(projectPath), configuration ?? "");
        if (_evaluations.TryGetValue(key, out var cached) && cached.IsCurrent())
            return cached.Response;

        lock (_lock)
        {
            // Another thread may have evaluated it while this one waited.
            if (_evaluations.TryGetValue(key, out cached) && cached.IsCurrent())
                return cached.Response;

            // The project's own time is read before evaluating, so a save made during the evaluation still counts.
            var projectStamp = FileStamp.Read(key.Item1);
            var response = Send(new HostRequest(projectPath, Configuration: configuration));
            _evaluations[key] = new CachedEvaluation(response,
                [projectStamp, .. (response.Imports ?? []).Select(FileStamp.Read)]);
            return response;
        }
    }

    /// <summary>Send a request that writes the project file, and forget its remembered evaluations.</summary>
    private HostResponse SendAndForget(HostRequest request)
    {
        var path = Path.GetFullPath(request.ProjectPath);
        try
        {
            return Send(request);
        }
        finally
        {
            // The write time alone could miss it: a coarse file-system clock can give the same time twice.
            foreach (var key in _evaluations.Keys.Where(key => key.Path == path))
                _evaluations.TryRemove(key, out _);
        }
    }

    private sealed record CachedEvaluation(HostResponse Response, FileStamp[] Files)
    {
        public bool IsCurrent() => Files.All(file => file.IsCurrent());
    }

    /// <summary>A file's write time when it was read (a missing file reads as the same fixed time).</summary>
    private readonly record struct FileStamp(string Path, DateTime WriteUtc)
    {
        public static FileStamp Read(string path) => new(path, File.GetLastWriteTimeUtc(path));
        public bool IsCurrent() => File.GetLastWriteTimeUtc(Path) == WriteUtc;
    }

    private HostResponse Send(HostRequest request)
    {
        lock (_lock)   // one request in flight at a time
        {
            try
            {
                return Query(request);
            }
            catch (IOException)
            {
                // Host died (or the pipe broke). Restart once and retry.
                Restart();
                return Query(request);
            }
        }
    }

    private HostResponse Query(HostRequest request)
    {
        if (_process is null || _process.HasExited) Restart();

        var requestJson = JsonSerializer.Serialize(request);
        _process!.StandardInput.WriteLine(requestJson);
        _process.StandardInput.Flush();

        var responseJson = _process.StandardOutput.ReadLine()
            ?? throw new IOException("MSBuild host process closed unexpectedly.");

        var response = JsonSerializer.Deserialize<HostResponse>(responseJson)
            ?? throw new InvalidOperationException("MSBuild host returned an unreadable response.");

        // A reported evaluation failure is NOT retried: it would just fail again.
        return response.Success
            ? response
            : throw new InvalidOperationException($"MSBuild host error: {response.Error}");
    }

    private void Kill()
    {
        if (_process is null) return;

        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception) { /* raced with exit */ }

        _process.Dispose();
        _process = null;
    }

    public void Dispose() => Kill();
}
