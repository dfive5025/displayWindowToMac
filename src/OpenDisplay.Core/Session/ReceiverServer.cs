using System.Net;
using System.Net.Sockets;
using OpenDisplay.Core.Video;
using OpenDisplay.Core.Wire;

namespace OpenDisplay.Core.Session;

public readonly record struct CursorState(bool Visible, double X, double Y);

/// <summary>Cursor sprite: PNG bytes, size normalized to the display, hotspot normalized to the sprite.</summary>
public sealed record CursorImage(byte[] Png, double NormalizedWidth, double NormalizedHeight, double HotspotX, double HotspotY);

public enum SessionState
{
    Waiting,
    Connected,
    Streaming,
}

/// <summary>
/// The receiving end of the OpenDisplay protocol: listens on TCP 9000, greets every
/// sender with <c>hello</c>, serves one sender at a time, decodes its video and
/// forwards input back to it. UI-agnostic; the Windows app subscribes to the events.
/// </summary>
public sealed class ReceiverServer : IDisposable
{
    public const int DefaultPort = 9000;

    private readonly object _gate = new();
    private readonly Func<HelloInfo> _hello;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ReceiverConnection> _connections = [];
    private TcpListener? _listener;
    private ReceiverConnection? _active;

    public ReceiverServer(Func<HelloInfo> hello, int port = DefaultPort)
    {
        _hello = hello;
        Port = port;
    }

    public int Port { get; private set; }

    /// <summary>Fires on the network thread. The handler owns the frame and must call <see cref="VideoFrame.Release"/>.</summary>
    public event Action<VideoFrame>? FrameDecoded;

    public event Action<CursorState>? CursorChanged;

    public event Action<CursorImage>? CursorImageChanged;

    public event Action<SessionState, string>? StateChanged;

    public event Action<string>? Log;

    public SessionState State { get; private set; } = SessionState.Waiting;

    public string? SenderAddress { get; private set; }

    /// <summary>Send a <c>hello</c> with HEVC in <c>videoCaps</c> (HEVC software decode costs more CPU).</summary>
    public bool OfferHevc { get; set; }

    internal HelloInfo CurrentHello() => _hello() with { OfferHevc = OfferHevc || _hello().OfferHevc };

    public void Start()
    {
        var listener = new TcpListener(IPAddress.IPv6Any, Port);
        listener.Server.DualMode = true;
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Write($"listening on TCP {Port}");
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                Write($"accept failed: {e.Message}");
                continue;
            }

            client.NoDelay = true;
            var conn = new ReceiverConnection(this, client);
            lock (_gate) _connections.Add(conn);
            _ = conn.RunAsync(ct);
        }
    }

    /// <summary>
    /// A connection proved itself by sending bytes: it becomes the live session and
    /// replaces the previous one (PROTOCOL.md section 1).
    /// </summary>
    internal bool Adopt(ReceiverConnection conn)
    {
        ReceiverConnection? old;
        lock (_gate)
        {
            if (_active == conn) return false;
            old = _active;
            _active = conn;
        }
        if (old != null) Write($"new sender {conn.Remote} replaces {old.Remote}");
        old?.Close("replaced by a new sender");
        SenderAddress = conn.Remote;
        SetState(SessionState.Connected, $"connected to {conn.Remote}");
        return true;
    }

    internal bool IsActive(ReceiverConnection conn)
    {
        lock (_gate) return _active == conn;
    }

    internal void Closed(ReceiverConnection conn, string reason)
    {
        bool wasActive;
        lock (_gate)
        {
            _connections.Remove(conn);
            wasActive = _active == conn;
            if (wasActive) _active = null;
        }
        if (wasActive)
        {
            Write($"session with {conn.Remote} ended: {reason}");
            SenderAddress = null;
            SetState(SessionState.Waiting, "waiting for a sender");
            CursorChanged?.Invoke(new CursorState(false, 0, 0));
        }
    }

    internal void SetState(SessionState state, string detail)
    {
        if (State == state && state != SessionState.Connected) return;
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    internal void RaiseFrame(VideoFrame frame)
    {
        var handler = FrameDecoded;
        if (handler == null) frame.Release();
        else handler(frame);
    }

    internal void RaiseCursor(CursorState c) => CursorChanged?.Invoke(c);

    internal void RaiseCursorImage(CursorImage img) => CursorImageChanged?.Invoke(img);

    internal void Write(string line) => Log?.Invoke(line);

    private ReceiverConnection? Active
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    /// <summary>Re-announce the panel, e.g. after the window moved to another monitor (PROTOCOL.md 6.1).</summary>
    public void PanelChanged()
    {
        ReceiverConnection[] all;
        lock (_gate) all = [.. _connections];
        foreach (var c in all) c.SendHello();
    }

    /// <summary>Finger/mouse input, normalized to the video (0..1, top-left origin).</summary>
    public void SendTouch(string phase, double x, double y) => Active?.SendTouch(phase, x, y);

    /// <summary>Scroll deltas in video pixels, natural-scrolling sign.</summary>
    public void SendScroll(double dx, double dy) => Active?.SendControl(Outgoing.Scroll(dx, dy));

    public void RequestKeyframe() => Active?.RequestKeyframe(force: true);

    /// <summary>Tell the sender we are going away for good, then close.</summary>
    public void Shutdown()
    {
        var a = Active;
        if (a != null)
        {
            a.SendControl(Outgoing.Closing());
            Thread.Sleep(100);
        }
        Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }
        ReceiverConnection[] all;
        lock (_gate) all = [.. _connections];
        foreach (var c in all) c.Close("receiver shutting down");
    }
}
