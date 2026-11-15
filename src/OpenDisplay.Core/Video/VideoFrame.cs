using System.Buffers;

namespace OpenDisplay.Core.Video;

/// <summary>
/// A decoded picture in BGRA (top-down, Stride = Width * 4). The pixel buffer is pooled:
/// whoever ends up holding the frame must call <see cref="Release"/> exactly once.
/// </summary>
public sealed class VideoFrame
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(maxArrayLength: 128 * 1024 * 1024, maxArraysPerBucket: 6);

    private byte[]? _pixels;

    private VideoFrame(byte[] pixels, int width, int height)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    /// <summary>Size of the decoded stream before any shrinking (video pixels, for scroll units).</summary>
    public int SourceWidth { get; internal set; }

    public int SourceHeight { get; internal set; }

    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VideoFrame));

    /// <summary>Sender clock capture time from the telemetry prefix, if any.</summary>
    public double? CaptureTimeMs { get; internal set; }

    public static VideoFrame Rent(int width, int height) =>
        new(Pool.Rent(width * height * 4), width, height);

    public void Release()
    {
        var p = Interlocked.Exchange(ref _pixels, null);
        if (p != null) Pool.Return(p);
    }
}
