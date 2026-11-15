using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenDisplay.Core.Keyboard;
using OpenDisplay.Core.Wire;
using Xunit;

namespace OpenDisplay.Core.Tests;

public class KeyboardTests
{
    [Theory]
    [InlineData(0x41, false, 0x1E, 0x00)] // A
    [InlineData(0x5A, false, 0x2C, 0x06)] // Z
    [InlineData(0x31, false, 0x02, 0x12)] // 1
    [InlineData(0x0D, false, 0x1C, 0x24)] // Enter
    [InlineData(0x0D, true, 0x1C, 0x4C)]  // keypad Enter
    [InlineData(0x08, false, 0x0E, 0x33)] // Backspace -> Delete
    [InlineData(0x25, true, 0x4B, 0x7B)]  // Left arrow
    [InlineData(0x10, false, 0x2A, KeyMap.MacShift)]
    [InlineData(0x10, false, 0x36, KeyMap.MacRightShift)]
    [InlineData(0x11, false, 0x1D, KeyMap.MacCommand)]  // left Ctrl -> Command
    [InlineData(0x11, true, 0x1D, KeyMap.MacControl)]   // right Ctrl -> Control
    [InlineData(0x12, false, 0x38, KeyMap.MacOption)]
    [InlineData(0x12, true, 0x38, KeyMap.MacRightOption)]
    public void MapsWindowsKeysToMac(int vk, bool extended, int scan, int mac) =>
        Assert.Equal(mac, KeyMap.ToMac(vk, extended, scan));

    [Fact]
    public void CtrlStaysControlWhenAsked() =>
        Assert.Equal(KeyMap.MacControl, KeyMap.ToMac(0x11, false, 0x1D, ctrlIsCommand: false));

    [Fact]
    public void UnknownKeysAreDropped() => Assert.Null(KeyMap.ToMac(0xE7 /* VK_PACKET */, false, 0));

    [Fact]
    public void HelloAdvertisesHighRefresh()
    {
        var json = Outgoing.Hello(new HelloInfo(2560, 1440, 1, "id", MaxFrameRate: 99));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(99, doc.RootElement.GetProperty("displayMaxFrameRate").GetInt32());
        foreach (var cap in doc.RootElement.GetProperty("videoCaps").EnumerateArray())
            Assert.Equal(99, cap.GetProperty("maxFrameRate").GetInt32());
    }

    /// <summary>A stand-in for mac/KeyBridge: same framing and auth.</summary>
    private sealed class FakeBridge : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly List<string> Received = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeBridge(string code)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        var s = c.GetStream();
                        var r = new FrameReader(s);
                        try
                        {
                            while (true)
                            {
                                using var f = await r.ReadFrameAsync(CancellationToken.None);
                                if (f == null) return;
                                var msg = ControlMessage.TryParse(f.Span)!;
                                if (msg.Type == "auth")
                                {
                                    bool ok = msg.String("code") == code;
                                    await s.WriteAsync(FrameWriter.Encode(System.Text.Encoding.UTF8.GetBytes(
                                        ok ? "{\"type\":\"authOk\"}" : "{\"type\":\"authFail\"}")));
                                    if (!ok) { c.Dispose(); return; }
                                    continue;
                                }
                                lock (Received) Received.Add(System.Text.Encoding.UTF8.GetString(f.Span));
                            }
                        }
                        catch { }
                    });
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task SendsKeysAfterPairing()
    {
        using var bridge = new FakeBridge("123456");
        using var client = new KeyBridgeClient();
        Assert.Equal(KeyBridgeStatus.Connected, await client.ConnectAsync(IPAddress.Loopback, "123456", "test", bridge.Port));
        client.SendKey(0x00, true);
        client.SendKey(0x00, false);
        client.ReleaseAll();
        for (int i = 0; i < 50; i++)
        {
            lock (bridge.Received) if (bridge.Received.Count >= 3) break;
            await Task.Delay(20);
        }
        lock (bridge.Received)
        {
            Assert.Equal("{\"type\":\"key\",\"code\":0,\"down\":true}", bridge.Received[0]);
            Assert.Equal("{\"type\":\"key\",\"code\":0,\"down\":false}", bridge.Received[1]);
            Assert.Equal("{\"type\":\"releaseAll\"}", bridge.Received[2]);
        }
    }

    [Fact]
    public async Task WrongCodeIsReported()
    {
        using var bridge = new FakeBridge("123456");
        using var client = new KeyBridgeClient();
        Assert.Equal(KeyBridgeStatus.BadCode, await client.ConnectAsync(IPAddress.Loopback, "000000", "test", bridge.Port));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task MissingBridgeIsReported()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var client = new KeyBridgeClient();
        Assert.Equal(KeyBridgeStatus.NotRunning, await client.ConnectAsync(IPAddress.Loopback, "123456", "test", port));
    }
}
