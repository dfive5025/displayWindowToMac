using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenDisplay.Core.Wire;

/// <summary>What this receiver tells the sender in <c>hello</c> (PROTOCOL.md 6.1, 6.5, 6.7).</summary>
public sealed record HelloInfo(
    int PixelsWide,
    int PixelsHigh,
    double Scale,
    string Id,
    string Device = "Windows",
    int MaxFrameRate = 60,
    bool OfferHevc = false);

/// <summary>Builds the receiver-to-sender control messages. Every one is a single UTF-8 JSON object.</summary>
public static class Outgoing
{
    public const int ProtocolVersion = 3;

    public static byte[] Hello(HelloInfo h)
    {
        return Write(w =>
        {
            w.WriteString("type", "hello");
            // Deprecated fields, still read by older senders.
            w.WriteNumber("pixelsWide", h.PixelsWide);
            w.WriteNumber("pixelsHigh", h.PixelsHigh);
            w.WriteNumber("scale", Math.Round(h.Scale, 3));
            w.WriteString("device", h.Device);
            w.WriteString("id", h.Id);
            w.WriteNumber("pv", ProtocolVersion);
            w.WriteNumber("displayMaxFrameRate", h.MaxFrameRate);
            w.WriteStartObject("panel");
            w.WriteNumber("pixelsWide", h.PixelsWide);
            w.WriteNumber("pixelsHigh", h.PixelsHigh);
            w.WriteNumber("scale", Math.Round(h.Scale, 3));
            w.WriteEndObject();
            w.WriteStartArray("videoCaps");
            w.WriteStartObject();
            w.WriteString("codec", "h264");
            w.WriteNumber("maxFrameRate", Math.Clamp(h.MaxFrameRate, 30, 120));
            w.WriteEndObject();
            if (h.OfferHevc)
            {
                int longSide = Math.Max(h.PixelsWide, h.PixelsHigh);
                w.WriteStartObject();
                w.WriteString("codec", "hevc");
                w.WriteNumber("maxWidth", longSide);
                w.WriteNumber("maxHeight", longSide);
                w.WriteNumber("maxFrameRate", Math.Clamp(h.MaxFrameRate, 30, 120));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    public static byte[] Ping(double t) => Write(w =>
    {
        w.WriteString("type", "ping");
        w.WriteNumber("t", Math.Round(t));
    });

    public static byte[] KeyframeRequest() => Encoding.UTF8.GetBytes("{\"type\":\"kf\"}");

    public static byte[] Closing() => Encoding.UTF8.GetBytes("{\"type\":\"closing\"}");

    public static byte[] Sleeping() => Encoding.UTF8.GetBytes("{\"type\":\"sleeping\"}");

    /// <param name="phase">began, moved, ended or cancelled.</param>
    /// <param name="x">Normalized 0..1 across the video, left to right.</param>
    /// <param name="y">Normalized 0..1 across the video, top to bottom.</param>
    /// <param name="senderClockMs">Event time in the sender's clock, when the clock offset is known.</param>
    public static byte[] Touch(string phase, double x, double y, double? senderClockMs) => Write(w =>
    {
        w.WriteString("type", "touch");
        w.WriteString("phase", phase);
        w.WriteNumber("x", Math.Round(Math.Clamp(x, 0, 1), 5));
        w.WriteNumber("y", Math.Round(Math.Clamp(y, 0, 1), 5));
        if (senderClockMs is double t) w.WriteNumber("t", Math.Round(t));
    });

    /// <summary>Scroll deltas in video pixels with natural-scrolling sign (PROTOCOL.md 6.1, 7).</summary>
    public static byte[] Scroll(double dx, double dy) => Write(w =>
    {
        w.WriteString("type", "scroll");
        w.WriteNumber("dx", Math.Round(dx, 2));
        w.WriteNumber("dy", Math.Round(dy, 2));
    });

    public static byte[] Stats(IReadOnlyDictionary<string, object> fields) => Write(w =>
    {
        w.WriteString("type", "stats");
        foreach (var (k, v) in fields)
        {
            switch (v)
            {
                case string s: w.WriteString(k, s); break;
                case bool b: w.WriteBoolean(k, b); break;
                case int i: w.WriteNumber(k, i); break;
                case long l: w.WriteNumber(k, l); break;
                case double d: w.WriteNumber(k, Math.Round(d, 2)); break;
                default: w.WriteString(k, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }
    });

    private static byte[] Write(Action<Utf8JsonWriter> body)
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
}

/// <summary>A parsed sender-to-receiver control message. Unknown types and fields are ignored by design.</summary>
public sealed class ControlMessage
{
    private ControlMessage(string type, JsonElement root)
    {
        Type = type;
        Root = root;
    }

    public string Type { get; }

    public JsonElement Root { get; }

    /// <summary>Parses a control payload. Returns null for anything unparseable or without a type (PROTOCOL.md 6).</summary>
    public static ControlMessage? TryParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
            return new ControlMessage(t.GetString()!, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public double? Number(string name) =>
        Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public string? String(string name) =>
        Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
