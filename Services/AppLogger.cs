using System.IO;

namespace DicomMover.Services;

public sealed class AppLogger
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _extension;
    private readonly object _sync = new();

    public AppLogger(string logFile)
    {
        var path = Path.GetFullPath(logFile);
        _directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("Не удалось определить каталог журнала.");
        _baseName = Path.GetFileNameWithoutExtension(path);
        _extension = Path.GetExtension(path);

        Directory.CreateDirectory(_directory);
    }

    public string CurrentLogPath => GetPath(DateTime.Now);

    public event Action<string>? MessageWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void CleanupOldLogs(int retentionDays)
    {
        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, $"{_baseName}-*{_extension}"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Очистка журнала не должна мешать запуску приложения.
            }
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (_sync)
            File.AppendAllText(GetPath(DateTime.Now), line + Environment.NewLine);

        MessageWritten?.Invoke(line);
    }

    private string GetPath(DateTime date) => Path.Combine(
        _directory,
        $"{_baseName}-{date:yyyy-MM-dd}{_extension}");
}
