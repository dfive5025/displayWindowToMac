namespace OpenDisplay.Core.Wire;

/// <summary>
/// NTP-style offset to the sender's clock from ping/pong (PROTOCOL.md 8.1):
/// keep the last 15 samples and trust the one with the smallest round trip.
/// </summary>
public sealed class ClockSync
{
    private readonly object _gate = new();
    private readonly Queue<(double Rtt, double Offset)> _samples = new();

    public static double NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Feed a pong. <paramref name="t1"/> is our ping time, <paramref name="mt"/> the sender's clock.</summary>
    public void AddSample(double t1, double mt, double t2)
    {
        double rtt = t2 - t1;
        if (rtt < 0 || rtt >= 2000) return;
        lock (_gate)
        {
            _samples.Enqueue((rtt, mt - (t1 + t2) / 2));
            while (_samples.Count > 15) _samples.Dequeue();
        }
    }

    public double? Offset
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count == 0) return null;
                return _samples.MinBy(s => s.Rtt).Offset;
            }
        }
    }

    public double? LastRtt
    {
        get
        {
            lock (_gate) return _samples.Count == 0 ? null : _samples.Last().Rtt;
        }
    }

    /// <summary>Our current time expressed in the sender's clock, once the offset is known.</summary>
    public double? SenderNow() => Offset is double o ? NowMs() + o : null;

    public void Reset()
    {
        lock (_gate) _samples.Clear();
    }
}
