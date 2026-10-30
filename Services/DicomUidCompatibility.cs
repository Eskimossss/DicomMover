using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DicomMover.Models;
using FellowOakDicom;

namespace DicomMover.Services;

internal static class DicomUidCompatibility
{
    public const string KeyPrefix = "SHA256:";

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string GenerateUid(string hash, string role)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes($"DicomMover.UidCompatibility.v1:{role}:{hash}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return "2.25." + new BigInteger(bytes, isUnsigned: true, isBigEndian: true).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsUsable(string? uid, string? sopClass)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64 || uid == sopClass) return false;
        var parts = uid.Split('.');
        if (parts.Length < 2 || parts[0] is not ("0" or "1" or "2")) return false;
        return parts.All(p => p.Length > 0 && (p.Length == 1 || p[0] != '0') && p.All(c => c is >= '0' and <= '9')) &&
            (parts[0] == "2" || (int.TryParse(parts[1], out var second) && second <= 39));
    }

    public static void PrepareMetadata(DicomMetadata metadata, string hash)
    {
        metadata.OriginalSopInstanceUid ??= metadata.SopInstanceUid;
        if (!IsUsable(metadata.SopInstanceUid, metadata.SopClassUid))
            metadata.SopInstanceUid = GenerateUid(hash, "instance");
        if (!IsUsable(metadata.StudyInstanceUid, metadata.SopClassUid))
            metadata.StudyInstanceUid = GenerateUid(hash, "study");
        if (!IsUsable(metadata.SeriesInstanceUid, metadata.SopClassUid))
            metadata.SeriesInstanceUid = GenerateUid(hash, "series");
    }

    // Mutates only the in-memory outgoing file, never the source on disk.
    public static IReadOnlyList<string> Apply(DicomFile file, string hash)
    {
        var changes = new List<string>();
        var sopClass = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
        if (string.IsNullOrWhiteSpace(sopClass)) throw new InvalidDataException("Отсутствует SOP Class UID; автоматически определить тип DICOM-объекта нельзя.");
        foreach (var (tag, role) in new[] { (DicomTag.SOPInstanceUID, "instance"), (DicomTag.StudyInstanceUID, "study"), (DicomTag.SeriesInstanceUID, "series") })
        {
            var original = file.Dataset.GetSingleValueOrDefault(tag, string.Empty);
            if (IsUsable(original, sopClass)) continue;
            var replacement = GenerateUid(hash, role);
            file.Dataset.AddOrUpdate(tag, replacement);
            changes.Add($"{tag}: {original} → {replacement}");
        }
        file.FileMetaInfo.AddOrUpdate(DicomTag.MediaStorageSOPClassUID, sopClass);
        file.FileMetaInfo.AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID,
            file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID));
        NormalizeDates(file.Dataset);
        return changes;

        void NormalizeDates(DicomDataset dataset)
        {
            foreach (var item in dataset.ToArray())
            {
                if (item is DicomSequence sequence)
                {
                    foreach (var child in sequence.Items) NormalizeDates(child);
                }
                else if (item is DicomElement element && element.ValueRepresentation == DicomVR.DA)
                {
                    var values = element.Get<string[]>();
                    var normalized = values.Select(v => DicomStudyDate.Normalize(v) ?? v).ToArray();
                    if (values.SequenceEqual(normalized)) continue;
                    dataset.AddOrUpdate(item.Tag, normalized);
                    changes.Add($"{item.Tag}: дата приведена к yyyyMMdd");
                }
            }
        }
    }
}
