using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DicomMover.Tests;

public sealed class StudyDateRangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DicomMoverRange-" + Guid.NewGuid().ToString("N"));
    public StudyDateRangeTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("20261123", "112959", false)]
    [InlineData("20261123", "113000", true)]
    [InlineData("20261123", "113000.123456", true)]
    [InlineData("20261124", "235959.999999", true)]
    [InlineData("20261125", "080000", true)]
    [InlineData("20261125", "080000.000001", false)]
    [InlineData("20261126", "070000", false)]
    [InlineData("20261124", null, true)]
    [InlineData("20261123", null, false)]
    [InlineData("20261125", null, false)]
    [InlineData("20261125", "250000", false)]
    public void ScanAndStoredQueueApplySameInclusiveRange(string date, string? time, bool expected)
    {
        var pacs = new PacsSettings { Id = "pacs" };
        var watch = Path.Combine(_dir, "watch");
        Directory.CreateDirectory(watch);
        var folder = new WatchFolderSettings
        {
            Id = "folder", Path = watch, PacsIds = [pacs.Id],
            SendStudiesFromDate = new DateTime(2026, 11, 23), SendStudiesFromTime = "11:30",
            SendStudiesToDate = new DateTime(2026, 11, 25), SendStudiesToTime = "08:00"
        };
        var db = new Database(Path.Combine(_dir, "queue.db"));
        // Exercise the existing queue independently from discovery.
        db.Enqueue("stored", "unused.dcm", "1.2.3", null, folder.Id, [pacs],
            new DicomMetadata { StudyDate = date, StudyTime = time ?? string.Empty });
        db.ApplyStudyDateFilters([folder]);
        Assert.Equal(expected, db.GetNextReadyDelivery() is not null);

        // Valid DICOM times also exercise real file discovery and persistence.
        if (time != "250000")
        {
            var file = CreateFile(Path.Combine(watch, "image.dcm"), date, time);
            var settings = new AppSettings { WatchFolders = [folder], PacsServers = [pacs], FileStableSeconds = 0 };
            new FolderMonitor(settings, db, new AppLogger(Path.Combine(_dir, "app.log"))).Scan(folder);
            db.ApplyStudyDateFilters([folder]);
            Assert.Equal(expected ? 2 : 0, db.GetRecent(20).Count);
            if (expected) Assert.Contains(db.GetRecent(20), q => q.FilePath == file);
        }
        folder.SendStudiesFromDate = null;
        folder.SendStudiesToDate = null;
        db.ApplyStudyDateFilters([folder]);
        Assert.NotNull(db.GetNextReadyDelivery());
    }

    [Theory]
    [InlineData(null, null, "20261120", null, true)]
    [InlineData(null, "08:00", "20261125", "080001", false)]
    [InlineData("11:30", null, "20261126", null, true)]
    [InlineData(null, null, "20261125", "235959.999999", true)]
    public void EmptyBoundsAndTimesHaveDefinedDefaults(string? fromTime, string? toTime, string date, string? time, bool expected)
    {
        var folder = new WatchFolderSettings
        {
            SendStudiesFromDate = fromTime is null ? null : new DateTime(2026, 11, 23), SendStudiesFromTime = fromTime,
            SendStudiesToDate = toTime is null && fromTime is not null ? null : new DateTime(2026, 11, 25), SendStudiesToTime = toTime
        };
        Assert.Equal(expected, DicomStudyDate.IsInRange(date, time, StudyDateRange.FromFolder(folder)));
    }

    [Fact]
    public void OldQueueReadsStudyTimeFromFileAndSettingsSurviveCopyAndReload()
    {
        var path = CreateFile(Path.Combine(_dir, "image.dcm"), "20261123", "114500");
        var pacs = new PacsSettings { Id = "pacs" };
        var folder = new WatchFolderSettings
        {
            Id = "folder", Path = _dir, PacsIds = [pacs.Id],
            SendStudiesFromDate = new DateTime(2026, 11, 23), SendStudiesFromTime = "11:30",
            SendStudiesToDate = new DateTime(2026, 11, 25), SendStudiesToTime = "08:00"
        };
        var db = new Database(Path.Combine(_dir, "queue.db"));
        db.Enqueue("old", path, "1.2.3", null, folder.Id, [pacs], new DicomMetadata { StudyDate = "20261123" });
        db.ApplyStudyDateFilters([folder]);
        Assert.NotNull(db.GetNextReadyDelivery());
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { WatchFolders = [folder], PacsServers = [pacs], SkipPacsDuplicates = true }.Copy());
        var loaded = store.Load();
        Assert.True(loaded.SkipPacsDuplicates);
        Assert.Equal(StudyDateRange.FromFolder(folder), StudyDateRange.FromFolder(Assert.Single(loaded.WatchFolders)));
    }

    [Theory]
    [InlineData("24:00", "08:00")]
    [InlineData("11:60", "08:00")]
    [InlineData("abc", "08:00")]
    [InlineData("11:30", "10:00")]
    public void InvalidOrReversedRangeIsRejected(string from, string to)
    {
        var folder = new WatchFolderSettings
        {
            Enabled = false, SendStudiesFromDate = new DateTime(2026, 11, 23), SendStudiesFromTime = from,
            SendStudiesToDate = new DateTime(2026, 11, 23), SendStudiesToTime = to
        };
        Assert.Throws<InvalidDataException>(() => new AppSettings { WatchFolders = [folder] }
            .Validate(throwOnDuplicatePacs: true, requireConfigured: false));
    }

    private static string CreateFile(string path, string date, string? time)
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyDate, date }
        };
        if (time is not null) dataset.Add(DicomTag.StudyTime, time);
        new DicomFile(dataset).Save(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
