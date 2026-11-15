using System.Buffers.Binary;

namespace OpenDisplay.Core.Tests;

/// <summary>Loads the recorded wire frames in Fixtures/ (made by tools/make_fixture.py).</summary>
internal static class Fixture
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Raw file bytes: [4-byte length][payload] records, exactly as on the socket.</summary>
    public static byte[] Wire(string name) => File.ReadAllBytes(PathOf(name));

    public static List<byte[]> Payloads(string name)
    {
        var bytes = Wire(name);
        var result = new List<byte[]>();
        int o = 0;
        while (o < bytes.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(o));
            result.Add(bytes.AsSpan(o + 4, len).ToArray());
            o += 4 + len;
        }
        return result;
    }

    public const string H264 = "testsrc-320x240.h264.odframes";
    public const string Hevc = "testsrc-320x240.hevc.odframes";
}
