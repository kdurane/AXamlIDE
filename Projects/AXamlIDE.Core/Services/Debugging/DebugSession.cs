using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Debugging;

public sealed partial class DebugSession : IDisposable
{
    private readonly Process _process;
    private readonly DebugAdapterTransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly SemaphoreSlim _requestWriteLock = new(1, 1);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _messagePump;
    private readonly ConcurrentDictionary<string, byte> _modules = new(StringComparer.Ordinal);
    private int _sequence;
    private bool _disposed;

    public IReadOnlyCollection<string> LoadedModules => _modules.Keys.ToArray();

    /// <summary>
    /// The debuggee's process id: from the adapter's "process" event, or from the IDE starting it in the terminal
    /// (SharpDbg's runInTerminal sends no process event).
    /// </summary>
    public int? ProcessId { get; private set; }

    /// <summary>The debuggee started: its process id, once, however it was learnt.</summary>
    public event Action<int>? ProcessStarted;

    public event Action<string>? OutputReceived;
    public event Action<JsonElement>? EventReceived;
    public event Action<Exception>? Failed;

    /// <summary>The debugger this session runs (SharpDbg or netcoredbg).</summary>
    public DebugAdapter Adapter { get; }

    /// <summary>
    /// Start the program in a terminal (the adapter's runInTerminal request, for an adapter that
    /// <see cref="DebugAdapter.RunsInTerminal"/>): its working folder, command line and extra environment; returns the
    /// started process's id, or null if it couldn't be started. Set before <see cref="StartAsync"/>; without it the
    /// program's console comes back through the adapter.
    /// </summary>
    public Func<TerminalLaunch, Task<int?>>? RunInTerminal { get; set; }

    public DebugSession(DebugAdapter adapter)
    {
        Adapter = adapter;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(adapter.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            }
        };
        foreach (var argument in adapter.Arguments)
            _process.StartInfo.ArgumentList.Add(argument);
        if (!_process.Start())
            throw new InvalidOperationException($"Could not start {adapter.Name} at '{adapter.FileName}'.");

        _transport = new DebugAdapterTransport(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, leaveOpen: true);
        _ = ReadStandardErrorAsync();
        _messagePump = PumpMessagesAsync();
    }

    /// <summary>Only the project's own code is stepped into and shown (the launch's justMyCode; Settings > Debugging).</summary>
    public bool JustMyCode { get; private set; } = true;

    public async Task StartAsync(
        string programPath,
        string workingDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<SourceBreakpoint>> breakpoints,
        IReadOnlyList<string> exceptionFilters,
        bool justMyCode = true,
        CancellationToken cancellationToken = default)
    {
        JustMyCode = justMyCode;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(TimeSpan.FromSeconds(45));
        var token = linkedCancellation.Token;

        using (await RequestAsync("initialize", new
        {
            clientID = "AXamlIDE",
            clientName = "AXamlIDE",
            adapterID = "coreclr",
            pathFormat = "path",
            linesStartAt1 = true,
            columnsStartAt1 = true
        }, token))
        {
        }

        var launch = RequestAsync("launch", new
        {
            name = Path.GetFileNameWithoutExtension(programPath),
            type = "coreclr",
            request = "launch",
            program = programPath,
            cwd = workingDirectory,
            args = Array.Empty<string>(),
            stopAtEntry = false,
            // In the IDE's terminal when the adapter can (a real console: input, colours), else through the adapter.
            console = Adapter.RunsInTerminal && RunInTerminal is not null ? "integratedTerminal" : "internalConsole",
            // Off: step into and stop in code that isn't the project's (with SharpDbg, decompiled: DecompiledSources).
            justMyCode
        }, token);
        _ = launch.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await _initialized.Task.WaitAsync(token);
        foreach (var pair in breakpoints)
            await SetBreakpointsAsync(pair.Key, pair.Value, token);
        await SetExceptionBreakpointsAsync(exceptionFilters, token);

        using (await RequestAsync("configurationDone", new { }, token))
        {
        }

        using (await launch)
        {
        }
    }

    /// <summary>Replace every breakpoint in one source file (DAP's setBreakpoints), with their conditions.</summary>
    public async Task<string> SetBreakpointsAsync(string sourcePath, IReadOnlyList<SourceBreakpoint> breakpoints, CancellationToken cancellationToken = default)
    {
        var distinct = breakpoints.GroupBy(b => b.Line).Select(g => g.First()).OrderBy(b => b.Line).ToArray();
        using var response = await RequestAsync("setBreakpoints", new
        {
            source = new { name = Path.GetFileName(sourcePath), path = sourcePath },
            // Only the fields that are set: an empty condition must not reach the adapter as "".
            breakpoints = distinct.Select(ToDap).ToArray(),
            lines = distinct.Select(b => b.Line).ToArray()
        }, cancellationToken);
        return response.RootElement.GetRawText();

        static Dictionary<string, object> ToDap(SourceBreakpoint breakpoint)
        {
            var dap = new Dictionary<string, object> { ["line"] = breakpoint.Line };
            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
                dap["condition"] = breakpoint.Condition.Trim();
            if (!string.IsNullOrWhiteSpace(breakpoint.HitCondition))
                dap["hitCondition"] = breakpoint.HitCondition.Trim();
            return dap;
        }
    }

    /// <summary>When to break on exceptions (DAP's setExceptionBreakpoints): <see cref="ExceptionFilters"/> values.</summary>
    public async Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken cancellationToken = default)
    {
        using var response = await RequestAsync("setExceptionBreakpoints", new { filters }, cancellationToken);
    }

    public async Task ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        using var response = await RequestAsync("continue", new { threadId }, cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await RequestAsync("disconnect", new { terminateDebuggee = true }, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException
                                   or ObjectDisposedException)
        {
            OutputReceived?.Invoke($"{Adapter.Name} disconnect: {ex.Message}");
        }

        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
    }

    private async Task<JsonDocument> RequestAsync(
        string command,
        object arguments,
        CancellationToken cancellationToken)
    {
        await _requestWriteLock.WaitAsync(cancellationToken);
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            if (!_pending.TryAdd(sequence, completion))
                throw new InvalidOperationException("Could not register the debugger request.");

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                seq = sequence,
                type = "request",
                command,
                arguments
            });
            Trace("->", payload);
            await _transport.WriteMessageAsync(payload, cancellationToken);
        }
        catch
        {
            _pending.TryRemove(sequence, out _);
            _requestWriteLock.Release();
            throw;
        }

        _requestWriteLock.Release();
        try
        {
            var response = await completion.Task.WaitAsync(cancellationToken);
            var root = response.RootElement;
            if (root.TryGetProperty("success", out var success) && !success.GetBoolean())
            {
                var message = root.TryGetProperty("message", out var error) ? error.GetString() : null;
                string? detail = null;
                if (root.TryGetProperty("body", out var body)
                    && body.TryGetProperty("error", out var err)
                    && err.TryGetProperty("format", out var fmt))
                    detail = fmt.GetString();

                var raw = root.GetRawText();   // grab before Dispose
                response.Dispose();
                throw new DebugAdapterException(command, (detail ?? message ?? "").Trim(),
                    $"'{command}' failed: {message} {detail}{Environment.NewLine}{raw}");
            }

            return response;
        }
        catch
        {
            _pending.TryRemove(sequence, out _);
            if (completion.Task.IsCompletedSuccessfully)
                completion.Task.Result.Dispose();
            throw;
        }
    }

    private void OnProcessStarted(int processId)
    {
        if (ProcessId == processId)
            return;
        ProcessId = processId;
        ProcessStarted?.Invoke(processId);
    }

    /// <summary>
    /// A request from the adapter (DAP's "reverse requests"). Only runInTerminal is answered with success: start the
    /// program in the IDE's terminal and reply with its process id. Anything else is refused.
    /// </summary>
    private async Task AnswerRequestAsync(JsonElement request)
    {
        var sequence = request.GetProperty("seq").GetInt32();
        var command = request.TryGetProperty("command", out var name) ? name.GetString() ?? "" : "";
        object? body = null;
        string? error = null;
        try
        {
            if (command == "runInTerminal" && RunInTerminal is { } run
                && request.TryGetProperty("arguments", out var arguments) && TerminalLaunch.From(arguments) is { } launch)
            {
                if (await run(launch) is { } processId)
                {
                    body = new { processId };
                    OnProcessStarted(processId);
                }
                else
                    error = "The program couldn't be started in the terminal.";
            }
            else
            {
                error = $"AXamlIDE doesn't handle '{command}'.";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var reply = error is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { seq = 0, type = "response", request_seq = sequence, success = true, command, body })
            : JsonSerializer.SerializeToUtf8Bytes(new { seq = 0, type = "response", request_seq = sequence, success = false, command, message = error });
        await _requestWriteLock.WaitAsync();
        try
        {
            Trace("->", reply);
            await _transport.WriteMessageAsync(reply, _shutdown.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The session is ending.
        }
        finally
        {
            _requestWriteLock.Release();
        }
    }

    private void RememberModule(JsonElement message)
    {
        if (message.TryGetProperty("body", out var body)
            && body.TryGetProperty("reason", out var reason) && reason.GetString() == "new"
            && body.TryGetProperty("module", out var module)
            && module.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } modulePath)
            _modules.TryAdd(modulePath, 0);
    }

    private async Task PumpMessagesAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested
                   && await _transport.ReadMessageAsync(_shutdown.Token) is { } payload)
            {
                using var message = JsonDocument.Parse(payload);
                Trace("<-", payload);
                var root = message.RootElement;
                if (!root.TryGetProperty("type", out var type))
                    throw new InvalidDataException($"{Adapter.Name} sent a DAP message without a type.");

                if (type.GetString() == "response")
                {
                    var requestSequence = root.GetProperty("request_seq").GetInt32();
                    if (_pending.TryRemove(requestSequence, out var completion))
                        completion.TrySetResult(JsonDocument.Parse(payload));
                }
                else if (type.GetString() == "request")
                {
                    _ = AnswerRequestAsync(root.Clone());   // not awaited: the reply may need more messages read first
                }
                else if (type.GetString() == "event")
                {
                    var eventName = root.GetProperty("event").GetString();
                    if (eventName == "initialized")
                        _initialized.TrySetResult();
                    else if (eventName == "module")
                        RememberModule(root);
                    else if (eventName == "process" && root.TryGetProperty("body", out var process)
                             && process.TryGetProperty("systemProcessId", out var pid) && pid.TryGetInt32(out var id))
                        OnProcessStarted(id);
                    EventReceived?.Invoke(root.Clone());
                }
            }

            if (!_shutdown.IsCancellationRequested && !_process.HasExited)
                throw new EndOfStreamException($"{Adapter.Name} closed its DAP output unexpectedly.");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_shutdown.IsCancellationRequested)
                Failed?.Invoke(ex);
            foreach (var pending in _pending.Values)
                pending.TrySetException(ex);
            _pending.Clear();
            _initialized.TrySetException(ex);
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                OutputReceived?.Invoke($"[{Adapter.Name}] {line}");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _transport.Dispose();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            OutputReceived?.Invoke($"Could not stop {Adapter.Name}: {ex.Message}");
        }
        _process.Dispose();
        _shutdown.Dispose();
    }

    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("AXAMLIDE_DAP_TRACE");   // e.g. /tmp/dap.log

    private static readonly object TraceLock = new();

    private static void Trace(string direction, byte[] json)
    {
        if (TracePath is null)
            return;
        lock (TraceLock)
            File.AppendAllText(TracePath, $"{direction} {Encoding.UTF8.GetString(json)}{Environment.NewLine}");
    }
}
