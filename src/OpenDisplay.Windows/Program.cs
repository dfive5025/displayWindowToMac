namespace OpenDisplay.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        bool fullscreen = args.Any(a => a.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(settings, fullscreen));
    }
}
