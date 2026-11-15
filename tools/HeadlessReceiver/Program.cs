// Console receiver without a window: logs the session and counts decoded frames.
// Handy for testing against a real or fake sender:  dotnet run --project tools/HeadlessReceiver [port] [seconds]
using OpenDisplay.Core.Session;
using OpenDisplay.Core.Wire;

int port = args.Length > 0 ? int.Parse(args[0]) : ReceiverServer.DefaultPort;
double seconds = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : double.PositiveInfinity;

using var server = new ReceiverServer(() => new HelloInfo(1920, 1080, 1, "HEADLESS-0001", "Windows"), port);
int frames = 0, total = 0;
string size = "?";
server.Log += l => Console.WriteLine($"[log] {l}");
server.StateChanged += (s, d) => Console.WriteLine($"[state] {s}: {d}");
server.CursorChanged += c => { };
server.FrameDecoded += f =>
{
    size = $"{f.Width}x{f.Height}";
    f.Release();
    Interlocked.Increment(ref frames);
    Interlocked.Increment(ref total);
};
server.Start();
var start = DateTime.UtcNow;
while ((DateTime.UtcNow - start).TotalSeconds < seconds)
{
    await Task.Delay(1000);
    Console.WriteLine($"[video] {Interlocked.Exchange(ref frames, 0)} fps, {size}, total {total}");
}
Console.WriteLine($"TOTAL_FRAMES={total}");
