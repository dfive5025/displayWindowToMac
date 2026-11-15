using System.Text.Json;

namespace OpenDisplay.Windows;

/// <summary>Per-install settings in %LOCALAPPDATA%\OpenDisplayReceiver\settings.json.</summary>
internal sealed class AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDisplayReceiver");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Stable install identity, sent as Bonjour TXT id and hello.id (PROTOCOL.md 2.1).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>Name shown in the Mac app's Connection menu.</summary>
    public string Name { get; set; } = Environment.MachineName;

    public int Port { get; set; } = 9000;

    /// <summary>Also offer HEVC. Off by default: software HEVC decode costs more CPU than H.264.</summary>
    public bool OfferHevc { get; set; }

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
        }
        catch (Exception)
        {
            // A broken file falls back to defaults below.
        }
        s ??= new AppSettings();
        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Guid.NewGuid().ToString().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(s.Name)) s.Name = Environment.MachineName;
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Settings are a convenience; the app works without saving them.
        }
    }
}
