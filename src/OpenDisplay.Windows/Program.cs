namespace OpenDisplay.Windows;

internal static class Program
{
    private static readonly string CrashLog = Path.Combine(AppSettings.Folder, "crash.log");

    [STAThread]
    private static void Main(string[] args)
    {
        // Never die silently: any startup or UI-thread error is written down and shown.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        try
        {
            ApplicationConfiguration.Initialize();
            var settings = AppSettings.Load();
            bool fullscreen = args.Any(a => a.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(settings, fullscreen));
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    private static void Report(Exception? e)
    {
        var text = e?.ToString() ?? "unknown error";
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(CrashLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
        MessageBox.Show(
            $"OpenDisplay Receiver gặp lỗi và sẽ đóng.\n\n{e?.Message}\n\nChi tiết đã lưu ở:\n{CrashLog}",
            "OpenDisplay Receiver", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}
