using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenDisplay.Core.Session;
using OpenDisplay.Core.Wire;
using Xunit;

namespace OpenDisplay.Core.Tests;

/// <summary>End-to-end over loopback: a fake sender talks to the real receiver.</summary>
public class SessionTests
{
    private static readonly HelloInfo Panel = new(1920, 1080, 1, "TEST-ID");

    private sealed class FakeSender : IAsyncDisposable
    {
        private readonly TcpClient _client = new();
        private NetworkStream _stream = null!;
        private FrameReader _reader = null!;

        public static async Task<FakeSender> ConnectAsync(int port)
        {
            var s = new FakeSender();
            await s._client.ConnectAsync("127.0.0.1", port);
            s._client.NoDelay = true;
            s._stream = s._client.GetStream();
            s._reader = new FrameReader(s._stream);
            return s;
        }

        public Task SendJsonAsync(string json) => SendAsync(Encoding.UTF8.GetBytes(json));

        public async Task SendAsync(byte[] payload) => await _stream.WriteAsync(FrameWriter.Encode(payload));

        /// <summary>Reads control messages until one of the given type shows up.</summary>
        public async Task<JsonElement> ExpectAsync(string type, int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (true)
            {
                using var frame = await _reader.ReadFrameAsync(cts.Token) ?? throw new EndOfStreamException();
                using var doc = JsonDocument.Parse(frame.Span.ToArray());
                if (doc.RootElement.GetProperty("type").GetString() == type) return doc.RootElement.Clone();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Task.CompletedTask;
        }
    }

    private static ReceiverServer StartServer()
    {
        var server = new ReceiverServer(() => Panel, port: 0);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Greets_with_hello_then_decodes_the_stream_and_sends_input_back()
    {
        using var server = StartServer();
        int decoded = 0;
        var allDecoded = new TaskCompletionSource();
        server.FrameDecoded += f =>
        {
            f.Release();
            if (Interlocked.Increment(ref decoded) == 30) allDecoded.TrySetResult();
        };

        await using var sender = await FakeSender.ConnectAsync(server.Port);
        var hello = await sender.ExpectAsync("hello");
        Assert.Equal("TEST-ID", hello.GetProperty("id").GetString());
        Assert.Equal(1920, hello.GetProperty("panel").GetProperty("pixelsWide").GetInt32());

        await sender.SendJsonAsync("{\"type\":\"welcome\",\"pv\":3,\"min\":1}");
        await sender.SendJsonAsync("{\"type\":\"streamConfig\",\"codec\":\"h264\",\"width\":320,\"height\":240,\"framesPerSecond\":60}");
        await sender.SendJsonAsync("{\"type\":\"cursor\",\"x\":0.5,\"y\":0.5,\"v\":1,\"s\":1}");
        foreach (var p in Fixture.Payloads(Fixture.H264)) await sender.SendAsync(p);

        await allDecoded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SessionState.Streaming, server.State);

        server.SendTouch("began", 0.1, 0.2);
        var touch = await sender.ExpectAsync("touch");
        Assert.Equal("began", touch.GetProperty("phase").GetString());
        Assert.Equal(0.1, touch.GetProperty("x").GetDouble(), 3);

        server.SendScroll(0, 40);
        var scroll = await sender.ExpectAsync("scroll");
        Assert.Equal(40, scroll.GetProperty("dy").GetDouble());
    }

    [Fact]
    public async Task Pings_every_two_seconds()
    {
        using var server = StartServer();
        await using var sender = await FakeSender.ConnectAsync(server.Port);
        await sender.ExpectAsync("hello");
        var ping = await sender.ExpectAsync("ping", 4000);
        Assert.True(ping.GetProperty("t").GetDouble() > 1_600_000_000_000);
    }

    [Fact]
    public async Task Asks_for_a_keyframe_when_joining_mid_stream()
    {
        using var server = StartServer();
        await using var sender = await FakeSender.ConnectAsync(server.Port);
        await sender.ExpectAsync("hello");
        var frames = Fixture.Payloads(Fixture.H264);
        await sender.SendAsync(frames[5]); // a P-frame with no keyframe before it
        await sender.ExpectAsync("kf");
    }

    [Fact]
    public async Task A_newcomer_that_speaks_replaces_the_live_sender()
    {
        using var server = StartServer();
        await using var first = await FakeSender.ConnectAsync(server.Port);
        await first.ExpectAsync("hello");
        await first.SendJsonAsync("{\"type\":\"welcome\",\"pv\":3,\"min\":1}");
        await WaitUntil(() => server.State == SessionState.Connected);
        string? firstAddress = server.SenderAddress;

        await using var second = await FakeSender.ConnectAsync(server.Port);
        await second.ExpectAsync("hello");
        await second.SendJsonAsync("{\"type\":\"welcome\",\"pv\":3,\"min\":1}");
        await WaitUntil(() => server.SenderAddress != firstAddress && server.SenderAddress != null);

        server.SendTouch("moved", 0.5, 0.5);
        await second.ExpectAsync("touch");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
