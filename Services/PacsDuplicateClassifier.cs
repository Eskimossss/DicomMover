using FellowOakDicom.Network;

namespace DicomMover.Services;

internal static class PacsDuplicateClassifier
{
    internal const string SkippedPrefix = "Дубликат пропущен: ";

    public static bool IsDuplicate(DicomStatus? status, string? errorComment = null)
    {
        if (status is null || status.State != DicomState.Failure) return false;
        if (status.Code == DicomStatus.DuplicateSOPInstance.Code) return true;
        // Duplicate invocation is a protocol error, not evidence that an image is stored.
        if (status.Code == DicomStatus.DuplicateInvocation.Code) return false;
        var text = string.IsNullOrWhiteSpace(errorComment) ? status.ToString() : errorComment;
        return new[] { "duplicate sop", "duplicate instance", "duplicate object", "duplicate study",
                "instance already exists", "object already exists", "sopinstanceuid already exists",
                "sop instance uid already exists", "дубликат экземпляра", "дубликат исследования" }
            .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
