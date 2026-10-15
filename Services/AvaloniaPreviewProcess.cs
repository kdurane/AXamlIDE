using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;

namespace AXaxmlIDE.Services;

public sealed class AvaloniaPreviewProcess : IDisposable
{
    private Process? _process;
    private IDisposable? _listener;
    private IAvaloniaRemoteTransportConnection? _connection;
    private WriteableBitmap? _bitmap;
    private readonly ConcurrentQueue<string> _processOutput = new();
    private bool _disposed;
    private double _scaling = 1;

    public bool IsRunning => _process is { HasExited: false };
    public bool IsReady => IsRunning && _connection is not null;
    public double Scaling => _scaling;
    public event Action<WriteableBitmap?>? FrameReceived;
    public event Action<string?>? ErrorChanged;

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

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        _bitmap = null;
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
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdateBitmap(frame));
                    await connection.Send(new FrameReceivedMessage { SequenceId = frame.SequenceId });
                    break;
                case UpdateXamlResultMessage update:
                    ErrorChanged?.Invoke(update.Exception?.Message ?? update.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[preview] Failed to process {message.GetType().Name}: {ex}");
            ErrorChanged?.Invoke(ex.Message);
        }
    }

    private void UpdateBitmap(FrameMessage frame)
    {
        var pixelFormat = frame.Format switch
        {
            Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888 => PixelFormats.Bgra8888,
            Avalonia.Remote.Protocol.Viewport.PixelFormat.Rgba8888 => PixelFormats.Rgba8888,
            _ => throw new NotSupportedException($"Unsupported preview pixel format '{frame.Format}'.")
        };

        if (_bitmap is null
            || _bitmap.PixelSize.Width != frame.Width
            || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap = new WriteableBitmap(
                new PixelSize(Math.Max(frame.Width, 1), Math.Max(frame.Height, 1)),
                new Vector(96, 96),
                pixelFormat,
                AlphaFormat.Unpremul);
        }

        if (frame is { Width: > 0, Height: > 0 })
        {
            using var framebuffer = _bitmap.Lock();
            var copyBytes = Math.Min(frame.Stride, framebuffer.RowBytes);
            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Data, row * frame.Stride,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), copyBytes);
        }

        FrameReceived?.Invoke(_bitmap);
    }

    private void OnConnectionException(IAvaloniaRemoteTransportConnection connection, Exception exception)
    {
        Debug.WriteLine($"[preview] Remote connection failed: {exception}");
        ErrorChanged?.Invoke(exception.Message);
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
        var output = string.Join(Environment.NewLine, _processOutput);
        var details = string.IsNullOrWhiteSpace(output)
            ? string.Empty
            : $"{Environment.NewLine}{output}";
        ErrorChanged?.Invoke($"Preview host exited with code {process.ExitCode}.{details}");
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
