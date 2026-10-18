using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Debugging;

public sealed class DebugAdapterTransport(Stream input, Stream output, bool leaveOpen = false) : IDisposable
{
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _readOffset;
    private int _readCount;
    private bool _disposed;

    public async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var headerBytes = 0;
        int? contentLength = null;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken);
            if (line is null)
            {
                if (headerBytes == 0)
                    return null;
                throw new EndOfStreamException("The debug adapter closed while sending DAP headers.");
            }

            headerBytes = checked(headerBytes + line.Length + 2);
            if (headerBytes > MaxHeaderBytes)
                throw new InvalidDataException("The debug adapter sent DAP headers that are too large.");

            if (line.Length == 0)
                break;

            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new InvalidDataException("The debug adapter sent a malformed DAP header.");

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            if (contentLength is not null
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length < 0
                || length > MaxMessageBytes)
                throw new InvalidDataException("The debug adapter sent an invalid DAP Content-Length.");

            contentLength = length;
        }

        if (contentLength is null)
            throw new InvalidDataException("The debug adapter omitted the DAP Content-Length header.");

        var payload = new byte[contentLength.Value];
        await ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    public async Task WriteMessageAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (payload.Length > MaxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "The DAP message is too large.");

        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(payload, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = await ReadByteAsync(cancellationToken);
            if (value < 0)
            {
                if (bytes.Count == 0)
                    return null;
                throw new EndOfStreamException("The debug adapter closed in the middle of a DAP header.");
            }

            if (value == '\n')
            {
                if (bytes.Count == 0 || bytes[^1] != '\r')
                    throw new InvalidDataException("DAP headers must use CRLF line endings.");
                bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add((byte)value);
            if (bytes.Count > MaxHeaderBytes)
                throw new InvalidDataException("The debug adapter sent DAP headers that are too large.");
        }
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_readOffset == _readCount)
        {
            _readCount = await input.ReadAsync(_readBuffer, cancellationToken);
            _readOffset = 0;
            if (_readCount == 0)
                return -1;
        }

        return _readBuffer[_readOffset++];
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (_readOffset < _readCount)
            {
                var count = Math.Min(_readCount - _readOffset, destination.Length - written);
                _readBuffer.AsMemory(_readOffset, count).CopyTo(destination[written..]);
                _readOffset += count;
                written += count;
                continue;
            }

            var countRead = await input.ReadAsync(destination[written..], cancellationToken);
            if (countRead == 0)
                throw new EndOfStreamException("The debug adapter closed in the middle of a DAP message.");
            written += countRead;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!leaveOpen)
        {
            input.Dispose();
            output.Dispose();
        }
        _writeLock.Dispose();
    }
}
