using DicomMover.Models;
using FellowOakDicom;

namespace DicomMover.Services;

public sealed record DicomMetadataValues(
    IReadOnlyList<string> Modalities,
    IReadOnlyList<string> StationNames,
    IReadOnlyList<string> Manufacturers,
    int FilesAnalyzed)
{
    public static DicomMetadataValues Empty { get; } = new([], [], [], 0);
}

public sealed class DicomMetadataDiscoveryService
{
    private const int MaxFiles = 200;

    public DicomMetadataValues Discover(WatchFolderSettings folder)
    {
        if (!Directory.Exists(folder.Path)) return DicomMetadataValues.Empty;

        var modalities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manufacturers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var analyzed = 0;
        foreach (var path in CandidateFiles(folder).Take(MaxFiles))
        {
            try
            {
                // Discovery хранит только три строки; Pixel Data не загружается и Dataset не удерживается.
                var file = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                var metadata = EncodingRuleResolver.ReadMetadata(file.Dataset);
                Add(modalities, metadata.Modality);
                Add(stations, metadata.StationName);
                Add(manufacturers, metadata.Manufacturer);
                analyzed++;
            }
            catch (DicomException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new(Sorted(modalities), Sorted(stations), Sorted(manufacturers), analyzed);
    }

    private static IEnumerable<string> CandidateFiles(WatchFolderSettings folder)
    {
        return SafeDirectoryEnumerator.EnumerateFiles(folder.Path, folder.SearchSubfolders)
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    private static void Add(IDictionary<string, string> values, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized) && !values.ContainsKey(normalized)) values[normalized] = normalized;
    }

    private static IReadOnlyList<string> Sorted(Dictionary<string, string> values) =>
        values.Values.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToList();
}
