using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// The ui_* tools' host process (Projects/AXamlIDE.UiHost): the user's app, set up headless, with one view open at a
/// time that the tools look at and click on. Started by ui_open on the app the .axaml file belongs to (the previewer's
/// target), and again when that's another app or a new build; stopped after <see cref="IdleTimeout"/> unused, and
/// with the IDE (it also stops on its own when its stdin closes).
///
/// Running the app's code is what opening the workspace already allowed (the trust prompt): the previews load the
/// same assemblies. The host has no OS windows and takes no OS input; clicks are calls into its own windows.
/// </summary>
public sealed class McpUiHost(XamlLanguageService xaml) : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int OutputLimit = 200;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentQueue<string> _output = new();
    private Process? _process;
    private TaskCompletionSource<JsonObject>? _ready;
    private XamlPreviewTarget? _target;
    private CancellationTokenSource? _idle;
    private int _nextId;
    private bool _disposed;

    /// <summary>A reply: its text (or error), a picture for screenshot, and what the app wrote or threw meanwhile.</summary>
    public sealed record Reply(string? Text, byte[]? Png, string? Error, IReadOnlyList<string> Exceptions, IReadOnlyList<string> Output, string? Note);

    /// <summary>Opens <paramref name="xamlPath"/>'s class in the app it belongs to (starting the host for that app's current build when needed). Call on the UI thread.</summary>
    public async Task<Reply> OpenAsync(string xamlPath, JsonObject request, CancellationToken ct)
    {
        var target = await xaml.GetPreviewTargetAsync(xamlPath);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? note = null;
            if (_process is not { HasExited: false } || target != _target)
                note = await StartAsync(target, ct);
            request["command"] = "open";
            request["assembly"] = target.XamlAssemblyPath;
            return await SendAsync(request, ct) with { Note = note };
        }
        finally
        {
            StopWhenIdle();
            _gate.Release();
        }
    }

    /// <summary>tree, act or screenshot, on what ui_open opened.</summary>
    public async Task<Reply> RequestAsync(JsonObject request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not { HasExited: false })
                return new Reply(null, null, "Nothing is open: call ui_open first" + (_target is null ? "." : " (the app's host stopped).") , [], [], null);
            var note = _target is { IsCurrent: false } ? "The app has been rebuilt since ui_open: this is still the old build (ui_open again for the new one)." : null;
            return await SendAsync(request, ct) with { Note = note };
        }
        finally
        {
            StopWhenIdle();
            _gate.Release();
        }
    }

    private async Task<Reply> SendAsync(JsonObject request, CancellationToken ct)
    {
        while (_output.TryDequeue(out _)) { }
        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await _process!.StandardInput.WriteLineAsync(request.ToJsonString());
            await _process.StandardInput.FlushAsync(ct);
            var answer = await reply.Task.WaitAsync(RequestTimeout, ct);
            return ToReply(answer, [.. _output]);
        }
        catch (TimeoutException)
        {
            Stop();
            return new Reply(null, null, $"The app didn't answer in {RequestTimeout.TotalSeconds:0} s (its UI thread may be stuck), so it was stopped. " +
                                         "ui_open starts it again.", [], [.. _output], null);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static Reply ToReply(JsonObject answer, List<string> output) => new(
        (string?)answer["text"],
        answer["png"] is JsonValue png ? Convert.FromBase64String((string)png!) : null,
        (string?)answer["error"],
        answer["exceptions"] is JsonArray exceptions ? [.. exceptions.Select(e => (string)e!)] : [],
        output,
        null);

    // ------------------------------------------------------------ the process

    /// <summary>Starts the host on the app's build; returns a note for the reply (packages fetched), or null.</summary>
    private async Task<string?> StartAsync(XamlPreviewTarget target, CancellationToken ct)
    {
        Stop();
        var app = target.ExecutableAssemblyPath;
        var appFolder = Path.GetDirectoryName(app)!;
        var name = Path.GetFileNameWithoutExtension(app);
        var depsFile = Path.Combine(appFolder, name + ".deps.json");
        var runtimeConfig = Path.Combine(appFolder, name + ".runtimeconfig.json");
        var host = Environment.GetEnvironmentVariable("AXAMLIDE_UIHOST") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "uihost", "AXamlIDE.UiHost.dll");
        if (!File.Exists(host))
            throw new InvalidOperationException($"The UI host isn't beside the IDE ({host}): rebuild the IDE.");

        var headless = await HeadlessPackages.FindAsync(depsFile, ct);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = appFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in (string[])["exec", "--runtimeconfig", runtimeConfig, "--depsfile", depsFile, host, "--app", app, "--headless", headless.HeadlessPath])
            start.ArgumentList.Add(argument);
        foreach (var folder in headless.ProbeFolders)
        {
            start.ArgumentList.Add("--probe");
            start.ArgumentList.Add(folder);
        }

        var ready = _ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start dotnet for the UI host.");
        _process = process;
        _target = target;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => OnReply(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        process.Exited += (_, _) => OnExited(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        JsonObject first;
        try
        {
            first = await ready.Task.WaitAsync(StartTimeout, ct);
        }
        catch (TimeoutException)
        {
            Stop();
            throw new InvalidOperationException("The UI host didn't start in time." + OutputTail());
        }
        if ((string?)first["error"] is { } error)
        {
            Stop();
            throw new InvalidOperationException(error + OutputTail());
        }
        return headless.Downloaded.Count > 0
            ? $"Downloaded {string.Join(", ", headless.Downloaded)} from nuget.org (to match the app's Avalonia {headless.AvaloniaVersion})."
            : null;
    }

    private void OnReply(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        JsonObject reply;
        try
        {
            reply = JsonNode.Parse(line)!.AsObject();
        }
        catch (JsonException)
        {
            OnOutput(line);
            return;
        }

        if (reply["id"] is JsonValue id && id.TryGetValue(out int number) && _pending.TryGetValue(number, out var waiting))
            waiting.TrySetResult(reply);
        else
            _ready?.TrySetResult(reply);
    }

    private void OnOutput(string? line)
    {
        if (line is null)
            return;
        _output.Enqueue(line);
        while (_output.Count > OutputLimit && _output.TryDequeue(out _)) { }
    }

    private void OnExited(Process process)
    {
        var exited = new JsonObject { ["error"] = $"The app's host stopped (exit code {SafeExitCode(process)})." + OutputTail() };
        _ready?.TrySetResult(exited);
        foreach (var waiting in _pending.Values)
            waiting.TrySetResult(exited);
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private string OutputTail()
    {
        var lines = _output.ToArray().TakeLast(15).ToList();
        return lines.Count == 0 ? "" : "\nIts output:\n" + string.Join('\n', lines);
    }

    private void StopWhenIdle()
    {
        _idle?.Cancel();
        var idle = _idle = new CancellationTokenSource();
        _ = Task.Delay(IdleTimeout, idle.Token).ContinueWith(async _ =>
        {
            await _gate.WaitAsync();
            try
            {
                if (!idle.IsCancellationRequested)
                    Stop();
            }
            finally
            {
                _gate.Release();
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;
        try
        {
            process.StandardInput.Close();   // the host's own way out
            if (!process.WaitForExit(1000))
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"[mcp] Stopping the UI host: {ex.Message}");
        }
        process.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _idle?.Cancel();
        Stop();
    }
}
