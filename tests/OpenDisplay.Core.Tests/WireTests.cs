using System.Text;
using System.Text.Json;
using OpenDisplay.Core.Wire;
using Xunit;

namespace OpenDisplay.Core.Tests;

public class WireTests
{
    /// <summary>Hands out at most one byte per read, like a slow socket.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct);
    }

    [Fact]
    public async Task Reassembles_frames_split_across_reads()
    {
        var a = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
        var b = new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3 };
        var stream = new TrickleStream([.. FrameWriter.Encode(a), .. FrameWriter.Encode(b)]);
        var reader = new FrameReader(stream);

        using var f1 = await reader.ReadFrameAsync(CancellationToken.None);
        using var f2 = await reader.ReadFrameAsync(CancellationToken.None);
        var end = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Equal(a, f1!.Span.ToArray());
        Assert.Equal(b, f2!.Span.ToArray());
        Assert.Null(end);
    }

    [Fact]
    public async Task Rejects_a_zero_length_frame()
    {
        var reader = new FrameReader(new MemoryStream([0, 0, 0, 0]));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stream_cut_inside_a_frame_is_an_error()
    {
        var reader = new FrameReader(new MemoryStream([0, 0, 0, 9, 1, 2]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public void Demux_follows_the_three_rules()
    {
        Assert.True(Demux.IsControl("{\"type\":\"cursor\"}"u8));
        Assert.False(Demux.IsControl("[1]"u8));
        Assert.False(Demux.IsControl(new byte[] { (byte)'{', 0, 0, 0, 1 }));
        var big = new byte[32768];
        big[0] = (byte)'{';
        Array.Fill(big, (byte)' ', 1, big.Length - 1);
        Assert.False(Demux.IsControl(big));
    }

    [Fact]
    public void Every_fixture_frame_is_video_and_carries_telemetry()
    {
        foreach (var p in Fixture.Payloads(Fixture.H264))
        {
            Assert.False(Demux.IsControl(p));
            Assert.True(AnnexB.FirstStartCode(p) > 0);
            var (cap, snd) = AnnexB.ReadTelemetry(p);
            Assert.NotNull(cap);
            Assert.NotNull(snd);
        }
    }

    [Fact]
    public void Finds_keyframes()
    {
        var h264 = Fixture.Payloads(Fixture.H264);
        Assert.True(AnnexB.IsKeyframe(h264[0], VideoCodec.H264));
        Assert.Equal([7, 8, 6, 5], AnnexB.NalTypes(h264[0], VideoCodec.H264));
        Assert.False(AnnexB.IsKeyframe(h264[1], VideoCodec.H264));

        var hevc = Fixture.Payloads(Fixture.Hevc);
        Assert.True(AnnexB.IsKeyframe(hevc[0], VideoCodec.Hevc));
        Assert.False(AnnexB.IsKeyframe(hevc[1], VideoCodec.Hevc));
    }

    [Fact]
    public void Hello_matches_the_protocol()
    {
        var json = Outgoing.Hello(new HelloInfo(2560, 1440, 1.5, "ABC", OfferHevc: true));
        Assert.True(Demux.IsControl(json));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("hello", r.GetProperty("type").GetString());
        Assert.Equal(3, r.GetProperty("pv").GetInt32());
        Assert.Equal("ABC", r.GetProperty("id").GetString());
        Assert.Equal(2560, r.GetProperty("panel").GetProperty("pixelsWide").GetInt32());
        Assert.Equal(1440, r.GetProperty("panel").GetProperty("pixelsHigh").GetInt32());
        Assert.Equal(1.5, r.GetProperty("panel").GetProperty("scale").GetDouble());
        var caps = r.GetProperty("videoCaps").EnumerateArray().Select(c => c.GetProperty("codec").GetString()).ToList();
        Assert.Equal(["h264", "hevc"], caps);
    }

    [Fact]
    public void Touch_and_scroll_use_wire_names()
    {
        using var touch = JsonDocument.Parse(Outgoing.Touch("began", 0.25, 1.4, null));
        Assert.Equal("touch", touch.RootElement.GetProperty("type").GetString());
        Assert.Equal(1.0, touch.RootElement.GetProperty("y").GetDouble()); // clamped into the video
        Assert.False(touch.RootElement.TryGetProperty("t", out _));

        using var scroll = JsonDocument.Parse(Outgoing.Scroll(0, -12));
        Assert.Equal(-12, scroll.RootElement.GetProperty("dy").GetDouble());
    }

    [Fact]
    public void Unknown_or_broken_control_messages_are_ignored()
    {
        Assert.Null(ControlMessage.TryParse("{not json"u8));
        Assert.Null(ControlMessage.TryParse("{\"no\":\"type\"}"u8));
        var m = ControlMessage.TryParse("{\"type\":\"futureThing\",\"x\":1}"u8);
        Assert.Equal("futureThing", m!.Type);
    }

    [Fact]
    public void Clock_offset_uses_the_fastest_sample()
    {
        var c = new ClockSync();
        c.AddSample(t1: 1000, mt: 6100, t2: 1200); // rtt 200, offset 5000
        c.AddSample(t1: 2000, mt: 7010, t2: 2020); // rtt 20, offset 5000
        c.AddSample(t1: 3000, mt: 9000, t2: 5500); // rtt too large, dropped
        Assert.Equal(5000, c.Offset);
    }
}
