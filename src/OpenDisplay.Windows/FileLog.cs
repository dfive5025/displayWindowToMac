namespace OpenDisplay.Windows;

/// <summary>Small rotating log in %LOCALAPPDATA%\OpenDisplayReceiver\receiver.log, plus the last lines in memory.</summary>
internal sealed class FileLog
{
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly string _path = Path.Combine(AppSettings.Folder, "receiver.log");

    public FileLog()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000)
                File.Move(_path, _path + ".1", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    public string FilePath => _path;

    public void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        lock (_gate)
        {
            _recent.Enqueue(stamped);
            while (_recent.Count > 12) _recent.Dequeue();
            try
            {
                File.AppendAllText(_path, stamped + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }

    public string[] Recent()
    {
        lock (_gate) return [.. _recent];
    }
}
