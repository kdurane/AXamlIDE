using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Remote.Protocol.Input;
using AXamlIDE.Models;
using AXamlIDE.Services.Preview;
using AXamlIDE.Services.Xaml;
using SkiaSharp;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// Renders AXAML to a PNG for the MCP server's render_xaml tool. It has its own designer host (not a tab's), so
/// rendering never changes what a preview pane shows. One host, for the project last rendered: it's restarted for
/// another project or a new build, after <see cref="RecycleAfterRenders"/> renders (a host never frees what it
/// compiled), and stopped after <see cref="IdleTimeout"/> with no render. Renders run one at a time.
/// </summary>
public sealed class McpPreviewRenderer(XamlLanguageService xaml) : IDisposable
{
    private const int RecycleAfterRenders = 50;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    /// <summary>How long a hover or click may take to change the picture before it's taken as changing nothing.</summary>
    private static readonly TimeSpan PointerFrameTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    /// <summary>The picture is the last frame before this long with no new one (the first frame can come before the content is drawn).</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);
    /// <summary>Something animating never settles: take what it shows by then.</summary>
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AvaloniaPreviewProcess? _process;
    private XamlPreviewTarget? _target;
    private int _renders;
    private CancellationTokenSource? _idle;
    private bool _disposed;

    /// <summary>What a render gave: the PNG and its size, or the host's error (with the line when it knows it).</summary>
    public sealed record Result(byte[]? Png, int Width, int Height, PreviewError? Error);

    /// <summary>
    /// The mouse, before the picture is taken: "hover" moves it to (X, Y), "press" also holds the left button down there,
    /// "click" presses and lets go. X and Y are in DIPs from the page's top-left (pixels of a render at scale 1).
    /// </summary>
    public sealed record Pointer(string Action, double X, double Y);

    /// <summary>Call on the UI thread (the target is worked out from the workspace's lists, which live there).</summary>
    public async Task<Result> RenderAsync(string path, string text, double scale, int maxSize, Pointer? pointer, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idle?.Cancel();

            var target = await xaml.GetPreviewTargetAsync(path);
            if (_process is not { IsReady: true } || target != _target || _renders >= RecycleAfterRenders)
            {
                _process?.Dispose();
                _process = new AvaloniaPreviewProcess();
                _target = target;
                _renders = 0;
                await _process.SetScalingAsync(scale);
                await _process.StartAsync(target, ct);
            }
            await _process.SetScalingAsync(scale);

            var outcome = await SendAsync(_process, target, path, text, ct);
            _renders++;
            return outcome;
        }
        finally
        {
            StopWhenIdle();
            _gate.Release();
        }

        // Frames that come after the update's result are its picture (the host answers an update before it draws it);
        // the last one before they stop coming is the finished picture. The same again after the mouse does something.
        async Task<Result> SendAsync(AvaloniaPreviewProcess process, XamlPreviewTarget target, string file, string xamlText,
            CancellationToken token)
        {
            var result = new TaskCompletionSource<PreviewError?>(TaskCreationOptions.RunContinuationsAsynchronously);
            RawImageData? latest = null;
            var frames = 0;
            void OnError(PreviewError? error) => result.TrySetResult(error);
            void OnFrame(RawImageData? image)   // on the UI thread, as this method is
            {
                if (image is null || !result.Task.IsCompleted)
                    return;
                latest = image;
                frames++;
            }

            process.ErrorChanged += OnError;
            process.FrameReceived += OnFrame;
            try
            {
                await process.UpdateXamlAsync(target.XamlAssemblyPath, file, xamlText);
                if (await result.Task.WaitAsync(ResultTimeout, token) is { } error)
                    return new Result(null, 0, 0, error);
                if (!await SettleAsync(FrameTimeout))
                    throw new TimeoutException("The designer host loaded the XAML but drew nothing.");

                if (pointer is not null)
                {
                    await SendPointerAsync(process, pointer);
                    await SettleAsync(PointerFrameTimeout);   // no new frame: the mouse changed nothing
                }

                var (png, width, height) = ToPng(latest!, maxSize);
                return new Result(png, width, height, null);
            }
            finally
            {
                process.ErrorChanged -= OnError;
                process.FrameReceived -= OnFrame;
                if (pointer is not null)
                    await ReleasePointerAsync(process, pointer);
            }

            // Wait for a new frame (false if none comes in time), then until they stop coming.
            async Task<bool> SettleAsync(TimeSpan firstFrameTimeout)
            {
                var seen = frames;
                var waiting = Stopwatch.StartNew();
                while (frames == seen)
                {
                    if (waiting.Elapsed > firstFrameTimeout)
                        return false;
                    await Task.Delay(PollInterval, token);
                }

                var settling = Stopwatch.StartNew();
                int before;
                do
                {
                    before = frames;
                    await Task.Delay(SettleTime, token);
                } while (frames != before && settling.Elapsed < SettleLimit);
                return true;
            }
        }
    }

    private static async Task SendPointerAsync(AvaloniaPreviewProcess process, Pointer pointer)
    {
        await process.SendInputAsync(new PointerMovedEventMessage { X = pointer.X, Y = pointer.Y });
        if (pointer.Action is "press" or "click")
            await process.SendInputAsync(new PointerPressedEventMessage { X = pointer.X, Y = pointer.Y, Button = MouseButton.Left });
        if (pointer.Action == "click")
            await process.SendInputAsync(new PointerReleasedEventMessage { X = pointer.X, Y = pointer.Y, Button = MouseButton.Left });
    }

    /// <summary>The next render starts with the mouse away and its button up.</summary>
    private static async Task ReleasePointerAsync(AvaloniaPreviewProcess process, Pointer pointer)
    {
        try
        {
            if (pointer.Action == "press")
                await process.SendInputAsync(new PointerReleasedEventMessage { X = pointer.X, Y = pointer.Y, Button = MouseButton.Left });
            await process.SendInputAsync(new PointerMovedEventMessage { X = -1, Y = -1 });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mcp] Couldn't reset the preview's mouse: {ex.Message}");
        }
    }

    /// <summary>The frame as a PNG, shrunk to fit <paramref name="maxSize"/> on its longer side (a picture costs the model context by its area).</summary>
    private static (byte[] Png, int Width, int Height) ToPng(RawImageData image, int maxSize)
    {
        var info = new SKImageInfo(image.Width, image.Height, image.ColorType, image.AlphaType);
        using var source = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(image.Bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            source.InstallPixels(info, handle.AddrOfPinnedObject(), image.RowBytes > 0 ? image.RowBytes : info.RowBytes);
            var longest = Math.Max(image.Width, image.Height);
            if (longest <= maxSize)
                return (Encode(source), image.Width, image.Height);

            var ratio = (double)maxSize / longest;
            var size = new SKImageInfo(Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio)),
                image.ColorType, image.AlphaType);
            using var scaled = source.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
            return (Encode(scaled), size.Width, size.Height);
        }
        finally
        {
            handle.Free();
        }

        static byte[] Encode(SKBitmap bitmap)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    private void StopWhenIdle()
    {
        var idle = _idle = new CancellationTokenSource();
        _ = Task.Delay(IdleTimeout, idle.Token).ContinueWith(async _ =>
        {
            await _gate.WaitAsync();
            try
            {
                if (idle.IsCancellationRequested || _disposed)
                    return;
                _process?.Dispose();
                _process = null;
                _target = null;
            }
            finally
            {
                _gate.Release();
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void Dispose()
    {
        _disposed = true;
        _idle?.Cancel();
        _process?.Dispose();
        _process = null;
    }
}
