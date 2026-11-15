namespace OpenDisplay.Core.Wire;

/// <summary>
/// The pv &lt;= 3 heuristic that tells a JSON control message from a video frame
/// (PROTOCOL.md section 4). Kept in one place so the pv 4 typed header is a cheap swap.
/// </summary>
public static class Demux
{
    public const int MaxControlLength = 32768;

    public static bool IsControl(ReadOnlySpan<byte> payload) =>
        payload.Length > 0
        && payload.Length < MaxControlLength
        && payload[0] == (byte)'{'
        && payload.IndexOf((byte)0) < 0;
}
