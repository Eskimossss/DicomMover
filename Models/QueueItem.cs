namespace DicomMover.Models;

public enum QueueStatus
{
    Pending,
    Sending,
    Partial,
    Sent,
    Failed,
    Exhausted
}

public sealed class QueueItem
{
    public long Id { get; set; }
    public string FileKey { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public QueueStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime DiscoveredAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? SopInstanceUid { get; set; }
    public string? PacsStatus { get; set; }
    public string? LastError { get; set; }
    public string? PatientName { get; set; }
    public string? FolderId { get; set; }
    public string? DeliverySummary { get; set; }
    public string? PatientId { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? Modality { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? AccessionNumber { get; set; }
    public string? StudyDate { get; set; }
}

public sealed class DicomMetadata
{
    public string SopInstanceUid { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? Modality { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? AccessionNumber { get; set; }
    public string? StudyDate { get; set; }
}

public sealed class DeliveryItem
{
    public long Id { get; set; }
    public long QueueItemId { get; set; }
    public string PacsId { get; set; } = string.Empty;
    public string PacsName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? SopInstanceUid { get; set; }
    public int AttemptCount { get; set; }
    public string? FolderId { get; set; }
}

public sealed class DeliveryDetails
{
    public long Id { get; set; }
    public string PacsName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? PacsStatus { get; set; }
    public string? LastError { get; set; }
}

public sealed record QueueCounts(
    long Pending,
    long Sending,
    long Sent,
    long Failed);
