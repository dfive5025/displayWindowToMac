namespace OpenDisplay.Core.Wire;

public enum VideoCodec
{
    H264,
    Hevc,
}

/// <summary>Helpers for the Annex B video payload (PROTOCOL.md section 5).</summary>
public static class AnnexB
{
    private static ReadOnlySpan<byte> StartCode => new byte[] { 0, 0, 0, 1 };

    /// <summary>
    /// Offset of the first 4-byte start code. Everything before it is the optional
    /// telemetry prefix. Returns -1 when the payload holds no start code.
    /// </summary>
    public static int FirstStartCode(ReadOnlySpan<byte> payload) => payload.IndexOf(StartCode);

    /// <summary>Reads the telemetry prefix ({"cap":ms,"snd":ms}) if there is one.</summary>
    public static (double? Cap, double? Snd) ReadTelemetry(ReadOnlySpan<byte> payload)
    {
        int start = FirstStartCode(payload);
        if (start <= 0) return (null, null);
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(payload[..start]);
            double? cap = null, snd = null;
            string? key = null;
            while (reader.Read())
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName) key = reader.GetString();
                else if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
                {
                    if (key == "cap") cap = reader.GetDouble();
                    else if (key == "snd") snd = reader.GetDouble();
                }
            }
            return (cap, snd);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>NAL unit types of every unit in the payload, in order.</summary>
    public static List<int> NalTypes(ReadOnlySpan<byte> payload, VideoCodec codec)
    {
        var types = new List<int>();
        int pos = FirstStartCode(payload);
        while (pos >= 0)
        {
            int header = pos + 4;
            if (header >= payload.Length) break;
            byte b = payload[header];
            types.Add(codec == VideoCodec.H264 ? b & 0x1F : (b >> 1) & 0x3F);
            int next = payload[header..].IndexOf(StartCode);
            pos = next < 0 ? -1 : header + next;
        }
        return types;
    }

    /// <summary>True when the access unit holds an IDR (H.264) or IRAP (HEVC) picture.</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> payload, VideoCodec codec)
    {
        foreach (int t in NalTypes(payload, codec))
        {
            if (codec == VideoCodec.H264 && t == 5) return true;
            if (codec == VideoCodec.Hevc && t >= 16 && t <= 21) return true;
        }
        return false;
    }
}
