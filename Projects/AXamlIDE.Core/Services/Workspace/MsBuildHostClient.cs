using System;
using System.Diagnostics;
using System.IO;
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

    public string[] GetProjectFiles(string projectPath)
        => GetProjectData(projectPath).Files;

    public string[] GetProjectReferences(string projectPath)
        => GetProjectData(projectPath).ProjectReferences;

    public string GetProjectAssemblyName(string projectPath)
        => GetProjectData(projectPath).AssemblyName
           ?? throw new InvalidOperationException($"MSBuild did not provide an assembly name for '{projectPath}'.");

    public string? GetProjectOutputType(string projectPath)
        => GetProjectData(projectPath).OutputType;

    public string? GetProjectOutputPath(string projectPath)
      => GetProjectData(projectPath).OutputPath?.Replace('\\', Path.DirectorySeparatorChar);

    public string? GetProjectAssetsFile(string projectPath)
        => GetProjectData(projectPath).ProjectAssetsFile;

    // ---- one file's build action and metadata (the Properties panel)

    public ProjectItemProperties GetItemProperties(string projectPath, string filePath)
        => ToProperties(Send(new HostRequest(projectPath, HostCommands.GetItem, filePath)));

    /// <summary>Change the file's build action (item type); writes the project file.</summary>
    public ProjectItemProperties SetItemType(string projectPath, string filePath, string itemType)
        => ToProperties(Send(new HostRequest(projectPath, HostCommands.SetItem, filePath, ItemType: itemType)));

    /// <summary>Change one metadata value of the file ("" clears it); writes the project file.</summary>
    public ProjectItemProperties SetItemMetadata(string projectPath, string filePath, string name, string value)
        => ToProperties(Send(new HostRequest(projectPath, HostCommands.SetItem, filePath, MetadataName: name, MetadataValue: value)));

    private static ProjectItemProperties ToProperties(HostResponse response)
        => response.Item is { } item
            ? new ProjectItemProperties(item.ItemType, item.AvailableItemTypes, item.Metadata)
            : throw new InvalidOperationException("MSBuild host returned no item.");

    private HostResponse GetProjectData(string projectPath) => Send(new HostRequest(projectPath));

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
