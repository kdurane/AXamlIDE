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

namespace AXamlIDE.Services
{
    public sealed class NetCoreDbgSession : IDisposable
    {
        private readonly Process _process;
        private readonly DebugAdapterTransport _transport;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
        private readonly SemaphoreSlim _requestWriteLock = new(1, 1);
        private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _messagePump;
        private int _sequence;
        private bool _disposed;

        public event Action<string>? OutputReceived;
        public event Action<JsonElement>? EventReceived;
        public event Action<Exception>? Failed;

        public static string ResolveExecutablePath()
        {
            var overridePath = Environment.GetEnvironmentVariable("AXAXMLIDE_NETCOREDBG_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                if (File.Exists(overridePath))
                    return overridePath;
                throw new FileNotFoundException(
                    $"AXAXMLIDE_NETCOREDBG_PATH points to a missing file: '{overridePath}'.", overridePath);
            }

            var executable = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
            var path = Environment.GetEnvironmentVariable("PATH");
            if (path is not null)
            {
                foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(directory, executable);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            throw new FileNotFoundException(
                "NetCoreDbg was not found on PATH. Install the NetCoreDbg standalone executable or set AXAXMLIDE_NETCOREDBG_PATH.");
        }

        public NetCoreDbgSession(string executablePath)
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo(executablePath)
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
            _process.StartInfo.ArgumentList.Add("--interpreter=vscode");
            _process.StartInfo.ArgumentList.Add("--engineLogging=/tmp/netcoredbg.log");
            if (!_process.Start())
                throw new InvalidOperationException($"Could not start NetCoreDbg at '{executablePath}'.");

            _transport = new DebugAdapterTransport(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, leaveOpen: true);
            _ = ReadStandardErrorAsync();
            _messagePump = PumpMessagesAsync();
        }

        public async Task StartAsync(
            string programPath,
            string workingDirectory,
            IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
            CancellationToken cancellationToken = default)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCancellation.CancelAfter(TimeSpan.FromSeconds(45));
            var token = linkedCancellation.Token;

            using (await RequestAsync("initialize", new
            {
                clientID = "AXaxmlIDE",
                clientName = "AXaxmlIDE",
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
                console = "internalConsole",
                justMyCode = true
            }, token);
            _ = launch.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await _initialized.Task.WaitAsync(token);
            foreach (var pair in breakpoints)
                await SetBreakpointsAsync(pair.Key, pair.Value, token);

            using (await RequestAsync("configurationDone", new { }, token))
            {
            }

            using (await launch)
            {
            }
        }

        public async Task SetBreakpointsAsync(
            string sourcePath,
            IReadOnlyList<int> lines,
            CancellationToken cancellationToken = default)
        {
            using var response = await RequestAsync("setBreakpoints", new
            {
                source = new { name = Path.GetFileName(sourcePath), path = sourcePath },
                breakpoints = lines.Distinct().Order().Select(line => new { line }).ToArray(),
                lines
            }, cancellationToken);
        }

        public async Task ContinueAsync(int threadId, CancellationToken cancellationToken = default)
        {
            using var response = await RequestAsync("continue", new { threadId }, cancellationToken);
        }

        public async Task<(string Path, int Line)?> GetTopFrameAsync(
            int threadId,
            CancellationToken cancellationToken = default)
        {
            using var response = await RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 1 },
                cancellationToken);
            var frame = response.RootElement.GetProperty("body").GetProperty("stackFrames").EnumerateArray()
                .FirstOrDefault();
            if (frame.ValueKind != JsonValueKind.Object
                || !frame.TryGetProperty("source", out var source)
                || !source.TryGetProperty("path", out var path)
                || !frame.TryGetProperty("line", out var line))
                return null;

            return (path.GetString()!, line.GetInt32());
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
                OutputReceived?.Invoke($"NetCoreDbg disconnect: {ex.Message}");
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
                    throw new InvalidOperationException(
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

        private async Task PumpMessagesAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested
                       && await _transport.ReadMessageAsync(_shutdown.Token) is { } payload)
                {
                    using var message = JsonDocument.Parse(payload);
                    var root = message.RootElement;
                    if (!root.TryGetProperty("type", out var type))
                        throw new InvalidDataException("NetCoreDbg sent a DAP message without a type.");

                    if (type.GetString() == "response")
                    {
                        var requestSequence = root.GetProperty("request_seq").GetInt32();
                        if (_pending.TryRemove(requestSequence, out var completion))
                            completion.TrySetResult(JsonDocument.Parse(payload));
                    }
                    else if (type.GetString() == "event")
                    {
                        var eventName = root.GetProperty("event").GetString();
                        if (eventName == "initialized")
                            _initialized.TrySetResult();
                        EventReceived?.Invoke(root.Clone());
                    }
                }

                if (!_shutdown.IsCancellationRequested && !_process.HasExited)
                    throw new EndOfStreamException("NetCoreDbg closed its DAP output unexpectedly.");
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
                    OutputReceived?.Invoke($"[NetCoreDbg] {line}");
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
                OutputReceived?.Invoke($"Could not stop NetCoreDbg: {ex.Message}");
            }
            _process.Dispose();
            _shutdown.Dispose();
        }
    }
}
