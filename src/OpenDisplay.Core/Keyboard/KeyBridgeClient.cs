using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenDisplay.Core.Wire;

namespace OpenDisplay.Core.Keyboard;

public enum KeyBridgeStatus
{
    Off,
    Connecting,
    Connected,
    /// <summary>Nothing listens on the Mac: KeyBridge is not running.</summary>
    NotRunning,
    /// <summary>KeyBridge answered but the pairing code is wrong or missing.</summary>
    BadCode,
}

/// <summary>
/// Client for KeyBridge, the small Mac helper that types keys sent from Windows
/// (see mac/KeyBridge/main.swift for the wire format).
/// </summary>
public sealed class KeyBridgeClient : IDisposable
{
    public const int DefaultPort = 9100;

    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public KeyBridgeStatus Status { get; private set; } = KeyBridgeStatus.Off;

    public event Action<KeyBridgeStatus, string>? StatusChanged;

    public bool IsConnected => Status == KeyBridgeStatus.Connected;

    /// <summary>Connects and authenticates. Returns the resulting status.</summary>
    public async Task<KeyBridgeStatus> ConnectAsync(IPAddress mac, string code, string name, int port = DefaultPort)
    {
        Disconnect(KeyBridgeStatus.Off, "reconnecting");
        SetStatus(KeyBridgeStatus.Connecting, $"connecting to {mac}:{port}");
        var cts = new CancellationTokenSource();
        var client = new TcpClient(mac.AddressFamily) { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(mac, port, timeout.Token).ConfigureAwait(false);
            }
            var stream = client.GetStream();
            await WriteAsync(stream, Json(w =>
            {
                w.WriteString("type", "auth");
                w.WriteString("code", code.Trim());
                w.WriteString("name", name);
            }), cts.Token).ConfigureAwait(false);

            var reader = new FrameReader(stream);
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var reply = await reader.ReadFrameAsync(authTimeout.Token).ConfigureAwait(false);
            var msg = reply == null ? null : ControlMessage.TryParse(reply.Span);
            if (msg?.Type != "authOk")
            {
                client.Dispose();
                SetStatus(KeyBridgeStatus.BadCode, "KeyBridge rejected the pairing code");
                return Status;
            }

            lock (_gate)
            {
                _client = client;
                _stream = stream;
                _cts = cts;
            }
            SetStatus(KeyBridgeStatus.Connected, $"keyboard linked to {mac}");
            _ = KeepAliveAsync(stream, reader, cts.Token);
            return Status;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or InvalidDataException)
        {
            client.Dispose();
            SetStatus(KeyBridgeStatus.NotRunning, $"KeyBridge not reachable on {mac}:{port} ({e.Message})");
            return Status;
        }
    }

    private async Task KeepAliveAsync(NetworkStream stream, FrameReader reader, CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        var readTask = Task.Run(async () =>
        {
            // KeyBridge sends nothing after authOk; a completed read means the Mac closed the link.
            using var _ = await reader.ReadFrameAsync(ct).ConfigureAwait(false);
        }, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = Task.Delay(2000, ct);
                if (await Task.WhenAny(delay, readTask).ConfigureAwait(false) == readTask) break;
                await WriteAsync(stream, ping, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        if (!ct.IsCancellationRequested) Disconnect(KeyBridgeStatus.NotRunning, "KeyBridge closed the connection");
    }

    /// <param name="macKeyCode">macOS virtual key code (see <see cref="KeyMap"/>).</param>
    public void SendKey(int macKeyCode, bool down) => Send(Json(w =>
    {
        w.WriteString("type", "key");
        w.WriteNumber("code", macKeyCode);
        w.WriteBoolean("down", down);
    }));

    /// <summary>Lift every key KeyBridge thinks is held (e.g. when the window loses focus).</summary>
    public void ReleaseAll() => Send(Encoding.UTF8.GetBytes("{\"type\":\"releaseAll\"}"));

    private void Send(byte[] json)
    {
        NetworkStream? stream;
        CancellationToken ct;
        lock (_gate)
        {
            stream = _stream;
            ct = _cts?.Token ?? CancellationToken.None;
        }
        if (stream == null) return;
        _ = SendAsync(stream, json, ct);
    }

    private async Task SendAsync(NetworkStream stream, byte[] json, CancellationToken ct)
    {
        try
        {
            await WriteAsync(stream, json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Disconnect(KeyBridgeStatus.NotRunning, "KeyBridge connection lost");
        }
    }

    private async Task WriteAsync(NetworkStream stream, byte[] json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(FrameWriter.Encode(json), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Disconnect(KeyBridgeStatus status, string detail)
    {
        TcpClient? client;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            client = _client;
            cts = _cts;
            _client = null;
            _stream = null;
            _cts = null;
        }
        cts?.Cancel();
        client?.Dispose();
        if (client != null || status != KeyBridgeStatus.Off) SetStatus(status, detail);
    }

    private void SetStatus(KeyBridgeStatus status, string detail)
    {
        Status = status;
        StatusChanged?.Invoke(status, detail);
    }

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public void Dispose() => Disconnect(KeyBridgeStatus.Off, "closed");
}
