namespace DicomMover.Models;

public sealed class ProblemRecord
{
    public long Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public long? QueueItemId { get; init; }
    public string? TargetId { get; init; }
    public DateTime OccurredAtUtc { get; init; }
}

public sealed class ProblemRow
{
    public long Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string KindCode { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public long? QueueItemId { get; init; }
    public string? TargetId { get; init; }

    public static ProblemRow FromRecord(ProblemRecord problem) => new()
    {
        Id = problem.Id,
        Key = problem.Key,
        KindCode = problem.Kind,
        Type = problem.Kind switch
        {
            "Delivery" => "Доставка",
            "Encoding" => "Кодировка DICOM",
            "EncodingIrrecoverable" => "Исходные данные утрачены",
            "EncodingAmbiguous" => "Неоднозначная кодировка",
            "BadFile" => "Некорректный файл",
            "Folder" => "Папка",
            "Pacs" => "PACS",
            _ => problem.Kind
        },
        ObjectName = problem.ObjectName,
        Time = problem.OccurredAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
        Details = problem.Details,
        FilePath = problem.FilePath,
        QueueItemId = problem.QueueItemId,
        TargetId = problem.TargetId
    };
}
