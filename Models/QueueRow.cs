namespace DicomMover.Models;

public sealed class QueueRow
{
    public string FileName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public string Time { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;

    public static QueueRow FromItem(QueueItem item)
    {
        var time = item.SentAtUtc
            ?? item.LastAttemptAtUtc
            ?? item.DiscoveredAtUtc;

        return new QueueRow
        {
            FileName = Path.GetFileName(item.FilePath),
            Status = item.Status switch
            {
                QueueStatus.Pending => "Ожидает",
                QueueStatus.Sending => "Отправляется",
                QueueStatus.Sent => "Отправлено",
                QueueStatus.Failed => "Ошибка",
                _ => item.Status.ToString()
            },
            Attempts = item.AttemptCount,
            Time = time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
            Result = item.LastError ?? item.PacsStatus ?? string.Empty
        };
    }
}
