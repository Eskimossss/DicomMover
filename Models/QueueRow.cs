namespace DicomMover.Models;

public sealed class QueueRow
{
    public int RowNumber { get; set; }
    public long Id { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string PatientName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public string Time { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string PacsResults { get; init; } = string.Empty;
    public string Modality { get; init; } = string.Empty;
    public string StatusColor { get; init; } = "#9AA0A6";

    public static QueueRow FromItem(QueueItem item)
    {
        var time = item.SentAtUtc
            ?? item.LastAttemptAtUtc
            ?? item.DiscoveredAtUtc;

        return new QueueRow
        {
            Id = item.Id,
            FilePath = item.FilePath,
            FileName = Path.GetFileName(item.FilePath),
            PatientName = item.PatientName ?? string.Empty,
            Modality = item.Modality ?? string.Empty,
            Status = item.Status switch
            {
                QueueStatus.Pending => "Ожидает",
                QueueStatus.Sending => "Отправляется",
                QueueStatus.Partial => "Отправлен частично",
                QueueStatus.Sent => "Отправлено",
                QueueStatus.Failed => "Ошибка",
                QueueStatus.Exhausted => "Лимит попыток",
                _ => item.Status.ToString()
            },
            Attempts = item.AttemptCount,
            Time = time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
            Result = item.LastError ?? item.PacsStatus ?? string.Empty,
            PacsResults = item.DeliverySummary ?? string.Empty,
            StatusColor = item.Status switch
            {
                QueueStatus.Sent => "#55A868",
                QueueStatus.Failed or QueueStatus.Exhausted => "#D96C6C",
                QueueStatus.Partial => "#D9B45B",
                QueueStatus.Sending => "#5B8FD9",
                _ => "#AEB4BA"
            }
        };
    }
}
