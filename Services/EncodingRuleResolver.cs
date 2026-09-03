using DicomMover.Models;
using FellowOakDicom;

namespace DicomMover.Services;

public sealed class EncodingRuleResolver
{
    public EncodingRule? Resolve(IEnumerable<EncodingRule> rules, string? folderId, string pacsId)
        => Resolve(rules, folderId, pacsId, new DicomRuleMetadata(null, null, null));

    public EncodingRule? Resolve(IEnumerable<EncodingRule> rules, string? folderId, string pacsId, DicomRuleMetadata metadata)
    {
        return Candidates(rules, folderId, pacsId)
            .Select((rule, index) => new { Rule = rule, Index = index, Match = Evaluate(rule, metadata) })
            .Where(x => x.Match.Applies)
            .OrderByDescending(x => Specificity(x.Rule))
            .ThenBy(x => x.Index)
            .Select(x => x.Rule)
            .FirstOrDefault();
    }

    public EncodingRule? ResolveForFile(IEnumerable<EncodingRule> rules, string? folderId, string pacsId, string filePath)
    {
        var candidates = Candidates(rules, folderId, pacsId).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.All(rule => Specificity(rule) == 0)) return candidates[0];

        // Для выбора правила читаются только метаданные; Pixel Data пропускается.
        var file = DicomFile.Open(filePath, FileReadOption.SkipLargeTags);
        return Resolve(candidates, folderId, pacsId, ReadMetadata(file.Dataset));
    }

    public EncodingRuleApplication Evaluate(EncodingRule rule, DicomRuleMetadata metadata)
    {
        var conditions = ConditionsText(rule);
        var failures = new List<string>();
        Check(rule.MatchModality, "Modality", rule.Modality, metadata.Modality, failures);
        Check(rule.MatchStationName, "Station Name", rule.StationName, metadata.StationName, failures);
        Check(rule.MatchManufacturer, "Manufacturer", rule.Manufacturer, metadata.Manufacturer, failures);

        return failures.Count == 0
            ? new(rule.Name, conditions, true, "✓ Условия выполнены")
            : new(rule.Name, conditions, false, $"— Правило не применяется: {string.Join("; ", failures)}");
    }

    public static DicomRuleMetadata ReadMetadata(DicomDataset dataset) => new(
        Value(dataset, DicomTag.Modality),
        Value(dataset, DicomTag.StationName),
        Value(dataset, DicomTag.Manufacturer));

    public static int Specificity(EncodingRule rule) =>
        (rule.MatchModality ? 1 : 0) + (rule.MatchStationName ? 1 : 0) + (rule.MatchManufacturer ? 1 : 0);

    public static string ConditionsText(EncodingRule rule)
    {
        var parts = new List<string>();
        if (rule.MatchModality) parts.Add($"Modality={rule.Modality?.Trim()}");
        if (rule.MatchStationName) parts.Add($"Station Name={rule.StationName?.Trim()}");
        if (rule.MatchManufacturer) parts.Add($"Manufacturer={rule.Manufacturer?.Trim()}");
        return parts.Count == 0 ? "без дополнительных условий" : string.Join("; ", parts);
    }

    private static IEnumerable<EncodingRule> Candidates(IEnumerable<EncodingRule> rules, string? folderId, string pacsId) =>
        rules.Where(rule => rule.Enabled &&
            string.Equals(rule.SourceFolderId, folderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.DestinationPacsId, pacsId, StringComparison.OrdinalIgnoreCase));

    private static void Check(bool enabled, string name, string? expected, string? actual, ICollection<string> failures)
    {
        if (!enabled) return;
        if (string.IsNullOrWhiteSpace(actual))
            failures.Add($"{name} отсутствует или пуст");
        else if (!string.Equals(expected?.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase))
            failures.Add($"{name} = {actual.Trim()}");
    }

    private static string? Value(DicomDataset dataset, DicomTag tag) =>
        dataset.TryGetSingleValue<string>(tag, out var value) ? value?.Trim() : null;
    }
