using DicomMover.Models;

namespace DicomMover.Services;

public sealed class EncodingRuleResolver
{
    public EncodingRule? Resolve(IEnumerable<EncodingRule> rules, string? folderId, string pacsId)
    {
        var candidates = rules.Where(r => r.Enabled &&
            string.Equals(r.DestinationPacsId, pacsId, StringComparison.OrdinalIgnoreCase));

        return candidates.FirstOrDefault(r =>
            string.Equals(r.SourceFolderId, folderId, StringComparison.OrdinalIgnoreCase));
    }
}
