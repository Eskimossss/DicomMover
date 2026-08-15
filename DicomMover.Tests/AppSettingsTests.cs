using DicomMover.Models;
using Xunit;

namespace DicomMover.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(0, 3, 60, 5)]
    [InlineData(45, -1, 60, 5)]
    [InlineData(45, 3, 0, 5)]
    [InlineData(45, 3, 60, 0)]
    public void RejectsUnsafeIntervalsAndAttemptLimits(
        int scanInterval,
        int stableInterval,
        int retryInterval,
        int maxAttempts)
    {
        var settings = new AppSettings
        {
            ScanIntervalSeconds = scanInterval,
            FileStableSeconds = stableInterval,
            RetryFailedAfterSeconds = retryInterval,
            MaxSendAttempts = maxAttempts
        };

        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void RejectsOverlappingEnabledWatchRoots()
    {
        var pacs = new PacsSettings { Id = "pacs" };
        var root = Path.Combine(Path.GetTempPath(), "dicommover-root");
        var settings = new AppSettings
        {
            PacsServers = [pacs],
            WatchFolders =
            [
                new WatchFolderSettings { Name = "Root", Path = root, PacsIds = [pacs.Id] },
                new WatchFolderSettings { Name = "Child", Path = Path.Combine(root, "child"), PacsIds = [pacs.Id] }
            ]
        };

        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void AllowsSubfolderScanningForSingleWatchRoot()
    {
        var pacs = new PacsSettings { Id = "pacs" };
        var settings = new AppSettings
        {
            PacsServers = [pacs],
            WatchFolders = [new WatchFolderSettings { Name = "Root", Path = Path.GetTempPath(), SearchSubfolders = true, PacsIds = [pacs.Id] }]
        };

        settings.Validate();
    }

    [Fact]
    public void CopiesStudyDateFilterForWatchFolder()
    {
        var expected = new DateTime(2026, 8, 2);
        var pacs = new PacsSettings { Id = "pacs" };
        var settings = new AppSettings
        {
            PacsHealthCheckSeconds = 75,
            PacsServers = [pacs],
            WatchFolders = [new WatchFolderSettings { Name = "Root", Path = Path.GetTempPath(), PacsIds = [pacs.Id] }]
        };
        settings.WatchFolders[0].SendStudiesFromDate = expected;

        var copy = settings.Copy();

        Assert.Equal(expected, copy.WatchFolders[0].SendStudiesFromDate);
        Assert.Equal(75, copy.PacsHealthCheckSeconds);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(3601)]
    public void RejectsUnsafePacsHealthCheckInterval(int interval)
    {
        var pacs = new PacsSettings { Id = "pacs" };
        var settings = new AppSettings
        {
            PacsHealthCheckSeconds = interval,
            PacsServers = [pacs],
            WatchFolders = [new WatchFolderSettings { Name = "Root", Path = Path.GetTempPath(), PacsIds = [pacs.Id] }]
        };

        Assert.Throws<InvalidDataException>(settings.Validate);
    }
}
