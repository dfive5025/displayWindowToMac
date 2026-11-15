using FFmpeg.AutoGen;
using OpenDisplay.Core.Wire;

namespace OpenDisplay.Core.Video;

public sealed class DecodeException(string message) : Exception(message);

/// <summary>
/// Software H.264 / HEVC decode with FFmpeg, converted to BGRA. One wire frame is one
/// access unit, fed as one packet; the decoder runs in low-delay mode so a picture comes
/// out for every picture that goes in.
/// </summary>
public sealed unsafe class FfmpegVideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;

    public FfmpegVideoDecoder(VideoCodec codec)
    {
        FfmpegLoader.EnsureLoaded();
        Codec = codec;
        var id = codec == VideoCodec.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        AVCodec* decoder = ffmpeg.avcodec_find_decoder(id);
        if (decoder == null) throw new DecodeException($"FFmpeg has no {codec} decoder");

        _ctx = ffmpeg.avcodec_alloc_context3(decoder);
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        // Slice threads only: frame threading would hold pictures back and add latency.
        _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
        _ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 8);

        int r = ffmpeg.avcodec_open2(_ctx, decoder, null);
        if (r < 0) throw new DecodeException($"avcodec_open2 failed: {ErrorText(r)}");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    public VideoCodec Codec { get; }

    /// <summary>
    /// Decodes one access unit (Annex B, starting at a start code). Returns the pictures it
    /// produced, normally exactly one. Throws <see cref="DecodeException"/> when the data is bad.
    /// </summary>
    /// <summary>
    /// Largest picture the caller will show (e.g. the window). Pictures bigger than this are
    /// shrunk during colour conversion, aspect kept, which is far cheaper than scaling at paint time.
    /// Zero means full size.
    /// </summary>
    public (int Width, int Height) MaxOutput { get; set; }

    public List<VideoFrame> Decode(byte[] data, int offset, int length)
    {
        var output = new List<VideoFrame>(1);
        if (length <= 0) return output;
        fixed (byte* p = &data[offset])
        {
            _packet->data = p;
            _packet->size = length;
            // A packet without a buffer is copied by FFmpeg, so `data` may be reused once this returns.
            int sent = ffmpeg.avcodec_send_packet(_ctx, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sent < 0 && sent != -ffmpeg.EAGAIN)
                throw new DecodeException($"avcodec_send_packet: {ErrorText(sent)}");
        }

        string? error = null;
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (r == -ffmpeg.EAGAIN || r == ffmpeg.AVERROR_EOF) break;
            if (r < 0)
            {
                error = $"avcodec_receive_frame: {ErrorText(r)}";
                break;
            }
            try
            {
                if ((_frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0 || _frame->decode_error_flags != 0)
                    error = "decoder reported a corrupt picture";
                else
                    output.Add(ToBgra(_frame));
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
        if (error != null)
        {
            foreach (var f in output) f.Release();
            throw new DecodeException(error);
        }
        return output;
    }

    private VideoFrame ToBgra(AVFrame* frame)
    {
        int w = frame->width, h = frame->height;
        var (ow, oh) = OutputSize(w, h, MaxOutput);
        // AREA averages the source pixels: the right filter for shrinking a desktop full of text.
        int flags = ow == w && oh == h ? ffmpeg.SWS_POINT : ffmpeg.SWS_AREA;
        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)frame->format,
            ow, oh, AVPixelFormat.AV_PIX_FMT_BGRA, flags, null, null, null);
        if (_sws == null) throw new DecodeException($"no converter for pixel format {frame->format}");

        var result = VideoFrame.Rent(ow, oh);
        result.SourceWidth = w;
        result.SourceHeight = h;
        fixed (byte* dst = result.Pixels)
        {
            var dstData = new byte*[] { dst, null, null, null };
            var dstStride = new[] { ow * 4, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, frame->data.ToArray(), frame->linesize.ToArray(), 0, h, dstData, dstStride);
        }
        return result;
    }

    /// <summary>Fits (w, h) inside the limit, aspect kept, never enlarging; sizes stay even.</summary>
    public static (int Width, int Height) OutputSize(int w, int h, (int Width, int Height) max)
    {
        if (max.Width <= 0 || max.Height <= 0 || (w <= max.Width && h <= max.Height)) return (w, h);
        double scale = Math.Min((double)max.Width / w, (double)max.Height / h);
        int ow = Math.Max(2, (int)(w * scale) & ~1);
        int oh = Math.Max(2, (int)(h * scale) & ~1);
        return (ow, oh);
    }

    public static string ErrorText(int error)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"error {error}";
    }

    public void Dispose()
    {
        if (_packet != null)
        {
            var p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }
        if (_ctx != null)
        {
            var c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }
        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }
    }
}
