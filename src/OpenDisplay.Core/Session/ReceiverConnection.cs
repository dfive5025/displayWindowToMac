using System.Diagnostics;
using System.Net.Sockets;
using OpenDisplay.Core.Video;
using OpenDisplay.Core.Wire;

namespace OpenDisplay.Core.Session;

/// <summary>One TCP connection from a sender. Becomes the live session once it sends bytes.</summary>
internal sealed class ReceiverConnection
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NewcomerLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeyframeRetry = TimeSpan.FromMilliseconds(500);

    private readonly ReceiverServer _server;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ClockSync _clock = new();
    private readonly Stopwatch _sinceRx = Stopwatch.StartNew();
    private readonly Stopwatch _sinceKfRequest = new();
    private readonly HashSet<string> _loggedUnknown = [];

    private FfmpegVideoDecoder? _decoder;
    private VideoCodec _codec = VideoCodec.H264;
    private bool _needKeyframe = true;
    private long _lastCursorSeq;
    private int _closed;
    private int _senderPv = 1;

    // Stats for the sender's log.
    private int _framesSinceStats;
    private long _bytesSinceStats;
    private int _decodeErrors;
    private readonly Stopwatch _statsWindow = Stopwatch.StartNew();
    private readonly List<double> _decodeMs = [];

    public ReceiverConnection(ReceiverServer server, TcpClient client)
    {
        _server = server;
        _client = client;
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public string Remote { get; }

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
        var ct = linked.Token;
        string reason = "connection closed";
        _server.Write($"sender connected from {Remote}");
        SendHello();
        var timer = TimerLoopAsync(ct);
        try
        {
            var reader = new FrameReader(_stream);
            while (!ct.IsCancellationRequested)
            {
                using var frame = await reader.ReadFrameAsync(ct).ConfigureAwait(false);
                if (frame == null) break;
                _sinceRx.Restart();
                if (!_server.IsActive(this)) _server.Adopt(this);
                Handle(frame);
            }
        }
        catch (OperationCanceledException)
        {
            reason = _closeReason ?? "cancelled";
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            reason = _closeReason ?? e.Message;
        }
        catch (Exception e)
        {
            // Anything else (a missing FFmpeg DLL, a bug) must still end the session visibly.
            reason = $"{e.GetType().Name}: {e.Message}";
            _server.Write($"session error: {e}");
        }
        finally
        {
            Close(reason);
            _decoder?.Dispose();
            _decoder = null;
            await timer.ConfigureAwait(false);
        }
    }

    private string? _closeReason;

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _closeReason = reason;
        _cts.Cancel();
        try
        {
            _client.Close();
        }
        catch (SocketException)
        {
        }
        _server.Closed(this, reason);
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        var sincePing = Stopwatch.StartNew();
        bool first = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                bool active = _server.IsActive(this);
                if (first || sincePing.Elapsed >= PingInterval)
                {
                    first = false;
                    sincePing.Restart();
                    SendControl(Outgoing.Ping(ClockSync.NowMs()));
                }
                var limit = active ? SilenceLimit : NewcomerLimit;
                if (_sinceRx.Elapsed > limit)
                {
                    Close($"no data for {limit.TotalSeconds:0} s");
                    return;
                }
                if (active && _needKeyframe && _decoder != null) RequestKeyframe(force: false);
                if (active && _statsWindow.Elapsed >= TimeSpan.FromSeconds(5)) SendStats();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Handle(WireFrame frame)
    {
        if (Demux.IsControl(frame.Span))
        {
            var msg = ControlMessage.TryParse(frame.Span);
            if (msg != null) HandleControl(msg);
            return;
        }
        HandleVideo(frame);
    }

    private void HandleControl(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "pong":
                if (msg.Number("t") is double t && msg.Number("mt") is double mt)
                    _clock.AddSample(t, mt, ClockSync.NowMs());
                break;
            case "ping":
                break; // liveness only; the sender's ping wants no reply
            case "welcome":
                _senderPv = (int)(msg.Number("pv") ?? 1);
                _server.Write($"sender speaks protocol v{_senderPv}");
                break;
            case "streamConfig":
                var codec = msg.String("codec") == "hevc" ? VideoCodec.Hevc : VideoCodec.H264;
                _server.Write($"stream: {msg.String("codec")} {msg.Number("width")}x{msg.Number("height")} @ {msg.Number("framesPerSecond")} fps");
                if (codec != _codec || _decoder == null)
                {
                    _codec = codec;
                    ResetDecoder();
                }
                break;
            case "cursor":
                if (msg.Number("s") is double s)
                {
                    if ((long)s <= _lastCursorSeq) break;
                    _lastCursorSeq = (long)s;
                }
                bool visible = (msg.Number("v") ?? 0) != 0;
                _server.RaiseCursor(new CursorState(visible, msg.Number("x") ?? 0, msg.Number("y") ?? 0));
                break;
            case "cursorImg":
                var png = msg.String("png");
                if (png == null) break;
                try
                {
                    _server.RaiseCursorImage(new CursorImage(Convert.FromBase64String(png),
                        msg.Number("nw") ?? 0, msg.Number("nh") ?? 0, msg.Number("ax") ?? 0, msg.Number("ay") ?? 0));
                }
                catch (FormatException)
                {
                }
                break;
            case "updateRequired":
                _server.Write($"sender says: {msg.String("message")}");
                _server.SetState(SessionState.Connected, msg.String("message") ?? "update required");
                break;
            default:
                if (_loggedUnknown.Add(msg.Type)) _server.Write($"ignoring message type '{msg.Type}'");
                break;
        }
    }

    private void ResetDecoder()
    {
        _decoder?.Dispose();
        _decoder = null;
        _needKeyframe = true;
    }

    private void HandleVideo(WireFrame frame)
    {
        var span = frame.Span;
        int start = AnnexB.FirstStartCode(span);
        if (start < 0) return;

        if (_needKeyframe)
        {
            if (!AnnexB.IsKeyframe(span, _codec))
            {
                RequestKeyframe(force: false);
                return;
            }
        }

        try
        {
            _decoder ??= new FfmpegVideoDecoder(_codec);
            var sw = Stopwatch.StartNew();
            var pictures = _decoder.Decode(frame.Buffer, start, frame.Length - start);
            _decodeMs.Add(sw.Elapsed.TotalMilliseconds);
            if (pictures.Count > 0 && _needKeyframe)
            {
                _needKeyframe = false;
                _server.SetState(SessionState.Streaming, $"showing video from {Remote}");
            }
            var (cap, _) = AnnexB.ReadTelemetry(span);
            foreach (var p in pictures)
            {
                p.CaptureTimeMs = cap;
                _framesSinceStats++;
                _server.RaiseFrame(p);
            }
            _bytesSinceStats += frame.Length;
        }
        catch (DecodeException e)
        {
            _decodeErrors++;
            _server.Write($"decode error ({e.Message}), asking for a keyframe");
            ResetDecoder();
            RequestKeyframe(force: true);
        }
    }

    public void RequestKeyframe(bool force)
    {
        if (!force && _sinceKfRequest.IsRunning && _sinceKfRequest.Elapsed < KeyframeRetry) return;
        _sinceKfRequest.Restart();
        SendControl(Outgoing.KeyframeRequest());
    }

    public void SendHello() => SendControl(Outgoing.Hello(_server.CurrentHello()));

    public void SendTouch(string phase, double x, double y) =>
        SendControl(Outgoing.Touch(phase, x, y, _clock.SenderNow()));

    private void SendStats()
    {
        double secs = _statsWindow.Elapsed.TotalSeconds;
        _statsWindow.Restart();
        var fields = new Dictionary<string, object>
        {
            ["transport"] = "tcp",
            ["client"] = "windows",
            ["fps"] = _framesSinceStats / secs,
            ["mbps"] = _bytesSinceStats * 8 / secs / 1_000_000,
            ["decodeErrors"] = _decodeErrors,
            ["offsetKnown"] = _clock.Offset.HasValue,
        };
        if (_decodeMs.Count > 0)
        {
            _decodeMs.Sort();
            fields["dec50"] = _decodeMs[_decodeMs.Count / 2];
            fields["dec95"] = _decodeMs[Math.Min(_decodeMs.Count - 1, _decodeMs.Count * 95 / 100)];
        }
        if (_clock.LastRtt is double rtt) fields["rtt"] = rtt;
        _framesSinceStats = 0;
        _bytesSinceStats = 0;
        _decodeMs.Clear();
        SendControl(Outgoing.Stats(fields));
    }

    /// <summary>Queue a control message. Never blocks the caller; failures close the connection.</summary>
    public void SendControl(byte[] json)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        var bytes = FrameWriter.Encode(json);
        _ = WriteAsync(bytes);
    }

    private async Task WriteAsync(byte[] bytes)
    {
        try
        {
            await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Close("write failed");
        }
    }
}
