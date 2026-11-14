using System.Buffers;
using System.Buffers.Binary;

namespace OpenDisplay.Core.Wire;

/// <summary>One length-prefixed frame (PROTOCOL.md section 3). The buffer is rented; dispose to return it.</summary>
public sealed class WireFrame : IDisposable
{
    private byte[]? _buffer;

    internal WireFrame(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(WireFrame));

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buffer, null);
        if (b != null) ArrayPool<byte>.Shared.Return(b);
    }
}

/// <summary>Reassembles [4-byte big-endian length][payload] frames from a byte stream.</summary>
public sealed class FrameReader
{
    /// <summary>Anything larger than this is treated as a corrupt stream, not a frame.</summary>
    public const int MaxFrameLength = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[4];

    public FrameReader(Stream stream) => _stream = stream;

    /// <summary>Returns the next frame, or null when the peer closed the stream cleanly.</summary>
    public async Task<WireFrame?> ReadFrameAsync(CancellationToken ct)
    {
        if (!await ReadExactlyOrEndAsync(_header, 4, ct).ConfigureAwait(false)) return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(_header);
        if (length == 0 || length > MaxFrameLength)
            throw new InvalidDataException($"bad frame length {length}");
        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            if (!await ReadExactlyOrEndAsync(buffer, (int)length, ct).ConfigureAwait(false))
                throw new EndOfStreamException("stream ended inside a frame");
            return new WireFrame(buffer, (int)length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("stream ended inside a frame");
            }
            read += n;
        }
        return true;
    }
}

public static class FrameWriter
{
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        return bytes;
    }
}
