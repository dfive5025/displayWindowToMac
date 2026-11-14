using OpenDisplay.Core.Video;
using OpenDisplay.Core.Wire;
using Xunit;

namespace OpenDisplay.Core.Tests;

public class DecoderTests
{
    [Theory]
    [InlineData(Fixture.H264, VideoCodec.H264)]
    [InlineData(Fixture.Hevc, VideoCodec.Hevc)]
    public void Decodes_one_picture_per_access_unit(string file, VideoCodec codec)
    {
        using var decoder = new FfmpegVideoDecoder(codec);
        int pictures = 0;
        foreach (var p in Fixture.Payloads(file))
        {
            int start = AnnexB.FirstStartCode(p);
            foreach (var f in decoder.Decode(p, start, p.Length - start))
            {
                Assert.Equal(320, f.Width);
                Assert.Equal(240, f.Height);
                // testsrc is colourful: the frame must not be all black.
                Assert.Contains(f.Pixels.Take(f.Stride * f.Height), b => b > 32);
                f.Release();
                pictures++;
            }
        }
        // Low-delay decode: every access unit comes straight back out.
        Assert.Equal(30, pictures);
    }

    [Fact]
    public void Garbage_is_reported_not_crashed_on()
    {
        using var decoder = new FfmpegVideoDecoder(VideoCodec.H264);
        var junk = new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x33, 0xff, 0xff, 0xff, 0xff };
        try
        {
            var frames = decoder.Decode(junk, 0, junk.Length);
            foreach (var f in frames) f.Release();
        }
        catch (DecodeException)
        {
            // Either outcome is fine; what matters is no native crash.
        }
    }
}
