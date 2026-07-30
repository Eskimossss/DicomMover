using System.IO;

namespace DicomMover.Services;

public sealed class AppLogger
{
    private readonly string _path;
    private readonly object _sync = new();

    public AppLogger(string logFile)
    {
        _path = Path.GetFullPath(logFile);
        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public event Action<string>? MessageWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (_sync)
            File.AppendAllText(_path, line + Environment.NewLine);

        MessageWritten?.Invoke(line);
    }
}
