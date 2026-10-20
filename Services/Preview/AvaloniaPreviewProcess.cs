using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using AXamlIDE.Models;
using SkiaSharp;

namespace AXamlIDE.Services.Preview;

public sealed class AvaloniaPreviewProcess : IDisposable
{
    private Process? _process;
    private IDisposable? _listener;
    private IAvaloniaRemoteTransportConnection? _connection;
    private TaskCompletionSource? _connecting;   // StartAsync waiting for the host to connect
    private readonly ConcurrentQueue<string> _processOutput = new();
    private bool _disposed;
    private double _scaling = 1;

    public bool IsRunning => _process is { HasExited: false };
    public bool IsReady => IsRunning && _connection is not null;
    public double Scaling => _scaling;
    /// <summary>A new frame (its own pixel array, kept as it is), or null when the host stops.</summary>
    public event Action<RawImageData?>? FrameReceived;
    /// <summary>The last update's error, or null when it rendered.</summary>
    public event Action<PreviewError?>? ErrorChanged;

    public async Task StartAsync(XamlPreviewTarget target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            throw new InvalidOperationException("The Avalonia preview process is already running.");

        var appDirectory = Path.GetDirectoryName(target.ExecutableAssemblyPath)
            ?? throw new InvalidOperationException("The preview app assembly has no output directory.");
        var appName = Path.GetFileNameWithoutExtension(target.ExecutableAssemblyPath);
        var runtimeConfig = Path.Combine(appDirectory, appName + ".runtimeconfig.json");
        var depsFile = Path.Combine(appDirectory, appName + ".deps.json");
        EnsureFile(target.XamlAssemblyPath);
        EnsureFile(target.ExecutableAssemblyPath);
        EnsureFile(target.HostAppPath);
        EnsureFile(runtimeConfig);
        EnsureFile(depsFile);

        try
        {
            using var portListener = new TcpListener(IPAddress.Loopback, 0);
            portListener.Start();
            var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
            portListener.Stop();

            var connected = _connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _listener = new BsonTcpTransport().Listen(IPAddress.Loopback, port, async connection =>
            {
                try
                {
                    await InitializeConnectionAsync(connection);
                    connected.TrySetResult();
                }
                catch (Exception ex)
                {
                    connected.TrySetException(ex);
                }
            });

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = appDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add("--depsfile");
            startInfo.ArgumentList.Add(depsFile);
            startInfo.ArgumentList.Add(target.HostAppPath);
            startInfo.ArgumentList.Add("--transport");
            startInfo.ArgumentList.Add($"tcp-bson://127.0.0.1:{port}/");
            startInfo.ArgumentList.Add("--session-id");
            startInfo.ArgumentList.Add(Random.Shared.Next(1, int.MaxValue).ToString());
            startInfo.ArgumentList.Add("--method");
            startInfo.ArgumentList.Add("avalonia-remote");
            startInfo.ArgumentList.Add(target.ExecutableAssemblyPath);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Avalonia designer host process.");
            while (_processOutput.TryDequeue(out _)) { }
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += OnProcessOutput;
            _process.ErrorDataReceived += OnProcessOutput;
            _process.Exited += OnProcessExited;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public async Task UpdateXamlAsync(string xamlAssemblyPath, string xamlPath, string xaml)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The Avalonia preview process is not connected.");
        await connection.Send(new UpdateXamlMessage
        {
            AssemblyPath = xamlAssemblyPath,
            XamlFileProjectPath = xamlPath,
            Xaml = xaml
        });
    }

    public async Task SetScalingAsync(double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaling), "Preview scaling must be a finite positive value.");
        if (Math.Abs(_scaling - scaling) < 0.001)
            return;

        if (_connection is { } connection)
        {
            await connection.Send(new ClientRenderInfoMessage
            {
                DpiX = 96 * scaling,
                DpiY = 96 * scaling
            });
        }

        _scaling = scaling;
    }

    public void Stop()
    {
        _connecting?.TrySetCanceled();   // a start still waiting gives up now, not after its timeout
        _connecting = null;
        _listener?.Dispose();
        _listener = null;

        if (_connection is { } connection)
        {
            _connection = null;
            connection.OnMessage -= OnConnectionMessage;
            connection.OnException -= OnConnectionException;
            connection.Dispose();
        }

        var process = _process;
        _process = null;
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            process.OutputDataReceived -= OnProcessOutput;
            process.ErrorDataReceived -= OnProcessOutput;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Debug.WriteLine($"Could not stop preview process: {ex}");
            }
            finally
            {
                process.Dispose();
            }
        }

        FrameReceived?.Invoke(null);
    }

    private async Task InitializeConnectionAsync(IAvaloniaRemoteTransportConnection connection)
    {
        if (!IsRunning)
            throw new InvalidOperationException("The preview host exited before connecting.");

        _connection = connection;
        connection.OnMessage += OnConnectionMessage;
        connection.OnException += OnConnectionException;
        await connection.Send(new ClientSupportedPixelFormatsMessage
        {
            Formats =
            [
                Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888,
                          Avalonia.Remote.Protocol.Viewport.PixelFormat.Rgba8888
            ]
        });
        await connection.Send(new ClientRenderInfoMessage
        {
            DpiX = 96 * _scaling,
            DpiY = 96 * _scaling
        });
    }

    private void OnConnectionMessage(IAvaloniaRemoteTransportConnection connection, object message)
        => _ = ProcessMessageAsync(connection, message);

    private async Task ProcessMessageAsync(IAvaloniaRemoteTransportConnection connection, object message)
    {
        try
        {
            switch (message)
            {
                case FrameMessage frame:
                    try
                    {
                        if (ToImage(frame) is { } image)
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => FrameReceived?.Invoke(image));
                    }
                    finally
                    {
                        await connection.Send(new FrameReceivedMessage { SequenceId = frame.SequenceId });
                    }
                    break;
                case UpdateXamlResultMessage update:
                    ErrorChanged?.Invoke(update.Exception is { } details
                        ? PreviewError.From(details.Message ?? update.Error ?? "The XAML couldn't be loaded.", details.LineNumber, details.LinePosition)
                        : string.IsNullOrWhiteSpace(update.Error) ? null : PreviewError.From(update.Error, null, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] Failed to process {message.GetType().Name}: {ex}");
            ErrorChanged?.Invoke(PreviewError.From(ex.Message, null, null));
        }
    }

    /// <summary>
    /// The frame's pixels as they arrived: each message has its own array, so it is used as it is (no copy).
    /// An empty frame (nothing drawn yet) gives null and the last picture stays.
    /// </summary>
    private static RawImageData? ToImage(FrameMessage frame)
    {
        if (frame is not { Width: > 0, Height: > 0 })
            return null;

        var colorType = frame.Format switch
        {
            Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888 => SKColorType.Bgra8888,
            Avalonia.Remote.Protocol.Viewport.PixelFormat.Rgba8888 => SKColorType.Rgba8888,
            _ => throw new NotSupportedException($"Unsupported preview pixel format '{frame.Format}'.")
        };
        return new RawImageData(frame.Data, frame.Width, frame.Height, colorType, SKAlphaType.Unpremul, frame.Stride);
    }

    private void OnConnectionException(IAvaloniaRemoteTransportConnection connection, Exception exception)
    {
        Debug.WriteLine($"[preview] Remote connection failed: {exception}");
        ErrorChanged?.Invoke(new PreviewError("The preview lost its connection: " + exception.Message, null, null));
    }

    private void OnProcessOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        _processOutput.Enqueue(e.Data);
        while (_processOutput.Count > 8)
            _processOutput.TryDequeue(out _);
        Debug.WriteLine($"[preview host] {e.Data}");
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_process is not { } process) return;
        process.WaitForExit();
        _connecting?.TrySetException(new InvalidOperationException($"The preview host exited with code {process.ExitCode} before connecting."));
        var output = string.Join(Environment.NewLine, _processOutput);
        var details = string.IsNullOrWhiteSpace(output)
            ? string.Empty
            : $"{Environment.NewLine}{output}";
        ErrorChanged?.Invoke(new PreviewError($"The preview host exited with code {process.ExitCode}.{details}", null, null));
    }

    private static void EnsureFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Preview dependency not found: '{path}'.", path);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
