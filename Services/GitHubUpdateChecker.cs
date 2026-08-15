using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;

namespace DicomMover.Services;

public sealed record UpdateCheckResult(Version Version, string TagName, string ReleaseUrl, string? Notes);

public sealed class GitHubUpdateChecker
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/Eskimossss/DicomMover/releases/latest");

    public async Task<UpdateCheckResult> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DicomMover", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await client.GetAsync(LatestReleaseUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("GitHub не вернул номер версии.");
        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var version))
            throw new InvalidDataException($"Не удалось распознать версию релиза: {tag}.");

        return new UpdateCheckResult(
            version,
            tag,
            root.GetProperty("html_url").GetString() ?? "https://github.com/Eskimossss/DicomMover/releases",
            root.TryGetProperty("body", out var body) ? body.GetString() : null);
    }
}
