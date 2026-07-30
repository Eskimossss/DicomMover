namespace DicomMover.Models;

public enum QueueStatus
{
    Pending,
    Sending,
    Sent,
    Failed
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
}

public sealed record QueueCounts(
    long Pending,
    long Sending,
    long Sent,
    long Failed);
