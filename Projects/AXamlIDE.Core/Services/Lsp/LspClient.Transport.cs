using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Lsp;

/// <summary>JSON-RPC plumbing: the outgoing queue, request/response matching, and the stdio read/write loops.</summary>
public sealed partial class LspClient
{
    /// <summary>Send any notification, e.g. a server's custom one from an AfterInitialize hook.</summary>
    public void Notify(string method, object? @params)
        => Enqueue(new { jsonrpc = "2.0", method, @params });

    private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            Notify("$/cancelRequest", new { id });
        });
        Enqueue(new { jsonrpc = "2.0", id, method, @params });

        try { return await tcs.Task; }
        finally { _pending.TryRemove(id, out _); }
    }

    private void Enqueue(object message)
    {
        if (_disposed) return;

        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");

        var frame = new byte[header.Length + json.Length];
        header.CopyTo(frame, 0);
        json.CopyTo(frame, header.Length);

        _outbox.Writer.TryWrite(frame);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _stdin.WriteAsync(frame, _cts.Token);
                await _stdin.FlushAsync(_cts.Token);
            }
        }
        catch (Exception) { /* server gone or disposing */ }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var contentLength = -1;
                while (true)
                {
                    var line = await ReadHeaderLineAsync();
                    if (line is null) return;               // EOF: server exited
                    if (line.Length == 0) break;            // blank line ends the headers
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                }
                if (contentLength < 0) continue;

                var body = new byte[contentLength];
                await _stdout.ReadExactlyAsync(body, _cts.Token);

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    Dispatch(doc.RootElement);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    ServerLog?.Invoke("Bad message from server: " + ex.Message);
                }
            }
        }
        catch (Exception) { /* EOF / cancelled */ }
        finally
        {
            foreach (var p in _pending.Values)
                p.TrySetException(new IOException("The language server exited."));
        }
    }

    private async Task<string?> ReadHeaderLineAsync()
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await _stdout.ReadAsync(one, _cts.Token) == 0) return null;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
        }
        return sb.ToString();
    }
}
