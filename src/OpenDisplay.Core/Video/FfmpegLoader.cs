using System.Reflection;
using FFmpeg.AutoGen;

namespace OpenDisplay.Core.Video;

/// <summary>Points FFmpeg.AutoGen at the native FFmpeg 7 DLLs that ship next to the app.</summary>
public static class FfmpegLoader
{
    private static readonly object Gate = new();
    private static bool _loaded;

    public static string? LoadedFrom { get; private set; }

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            var baseDir = AppContext.BaseDirectory;
            string[] candidates =
            [
                Environment.GetEnvironmentVariable("OPENDISPLAY_FFMPEG_DIR") ?? "",
                baseDir,
                Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                Path.Combine(baseDir, "dll", "x64"),
            ];
            foreach (var dir in candidates)
            {
                if (dir.Length > 0 && Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*avcodec*").Any())
                {
                    ffmpeg.RootPath = dir;
                    LoadedFrom = dir;
                    break;
                }
            }

            // FFmpeg.AutoGen 7 binds its function pointers in DynamicallyLoadedBindings.Initialize().
            // Depending on the package build it runs on its own or must be called; calling it twice is harmless.
            var bindings = typeof(ffmpeg).Assembly.GetType("FFmpeg.AutoGen.DynamicallyLoadedBindings");
            bindings?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)
                ?.Invoke(null, null);

            // Fails loudly here, with a clear message, rather than deep inside the first decode.
            _ = ffmpeg.avcodec_version();
            _loaded = true;
        }
    }

    public static string Version()
    {
        EnsureLoaded();
        uint v = ffmpeg.avcodec_version();
        return $"libavcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
    }
}
