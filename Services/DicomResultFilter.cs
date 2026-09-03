using DicomMover.Models;

namespace DicomMover.Services;

public static class DicomResultFilter
{
    public static IReadOnlyList<string> UniqueValues<T>(IEnumerable<T> items, Func<T, string?> selector) =>
        items.Select(selector).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static List<DicomFileDiagnosticResult> Diagnostics(IEnumerable<DicomFileDiagnosticResult> files,
        int fileFilter, string? modality, string? stationName) => files
        .Where(file => fileFilter switch
        {
            1 => file.HasProblem,
            2 => file.RuleApplication?.Applies != false,
            3 => file.RuleApplication?.Applies == false,
            _ => true
        })
        .Where(file => Matches(file.Metadata, modality, stationName)).ToList();

    public static List<DicomFileConversionResult> Conversions(IEnumerable<DicomFileConversionResult> files,
        int fileFilter, string? modality, string? stationName) => files
        .Where(file => fileFilter switch
        {
            1 => file.HasProblem,
            2 => file.IsApplicable,
            3 => !file.IsApplicable,
            _ => true
        })
        .Where(file => Matches(file.Metadata, modality, stationName)).ToList();

    public static T? PreserveSelection<T>(IReadOnlyList<T> visible, string? currentPath, Func<T, string> pathSelector) =>
        visible.FirstOrDefault(item => string.Equals(pathSelector(item), currentPath, StringComparison.OrdinalIgnoreCase))
        ?? visible.FirstOrDefault();

    private static bool Matches(DicomRuleMetadata? metadata, string? modality, string? stationName) =>
        Match(metadata?.Modality, modality) && Match(metadata?.StationName, stationName);

    private static bool Match(string? actual, string? selected) => string.IsNullOrWhiteSpace(selected) ||
        !string.IsNullOrWhiteSpace(actual) && string.Equals(actual.Trim(), selected.Trim(), StringComparison.OrdinalIgnoreCase);
}
