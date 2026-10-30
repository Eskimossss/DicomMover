using System.Security.Cryptography;
using System.Text;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DicomMover.Tests;

public sealed class StudyDateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DicomMoverStudyDate-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Cutoff = new(2026, 9, 1);

    public StudyDateTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("20260901", "20260901")]
    [InlineData("2026.09.01", "20260901")]
    [InlineData("2024.02.29", "20240229")]
    [InlineData("2026.02.29", null)]
    [InlineData("20260230", null)]
    [InlineData("2026.13.01", null)]
    [InlineData("01.09.2026", null)]
    [InlineData("2026.9.1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizationAcceptsOnlySupportedCalendarDates(string? input, string? expected)
    {
        Assert.Equal(expected, DicomStudyDate.Normalize(input));
    }

    [Theory]
    [InlineData("20260901", true)]
    [InlineData("2026.09.01", true)]
    [InlineData("2026.09.02", true)]
    [InlineData("2026.08.31", false)]
    [InlineData("2026.02.30", false)]
    [InlineData("", false)]
    public void ScanUsesInclusiveCutoffWithoutChangingSource(string date, bool expectedQueued)
    {
        var watchPath = Path.Combine(_directory, "watch");
        Directory.CreateDirectory(watchPath);
        var path = Path.Combine(watchPath, "sample.dcm");
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyDate, "20260901" },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([1, 2, 3, 4]))
        };
        new DicomFile(dataset).Save(path);
        // Inject the vendor's nonstandard value into the serialized DA element;
        // the library intentionally refuses to create such values through its validated API.
        var bytes = File.ReadAllBytes(path);
        byte[] marker = [0x08, 0x00, 0x20, 0x00, 0x44, 0x41, 0x08, 0x00];
        var tagOffset = bytes.AsSpan().IndexOf(marker);
        Assert.True(tagOffset >= 0);
        var value = Encoding.ASCII.GetBytes(date);
        using (var output = new MemoryStream())
        {
            output.Write(bytes.AsSpan(0, tagOffset + 6));
            output.WriteByte((byte)value.Length);
            output.WriteByte(0);
            output.Write(value);
            output.Write(bytes.AsSpan(tagOffset + 16));
            File.WriteAllBytes(path, output.ToArray());
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var originalHash = SHA256.HashData(File.ReadAllBytes(path));
        var pacs = new PacsSettings { Id = "pacs" };
        var folder = new WatchFolderSettings { Id = "folder", Path = watchPath, PacsIds = [pacs.Id], SendStudiesFromDate = Cutoff };
        var database = new Database(Path.Combine(_directory, "queue.db"));
        var settings = new AppSettings { WatchFolders = [folder], PacsServers = [pacs], FileStableSeconds = 0 };
        var monitor = new FolderMonitor(settings, database, new AppLogger(Path.Combine(_directory, "app.log")));

        monitor.Scan(folder);
        database.ApplyStudyDateFilters([folder]);

        Assert.Equal(expectedQueued ? 1 : 0, database.GetRecent(10).Count);
        Assert.Equal(expectedQueued, database.GetNextReadyDelivery() is not null);
        if (expectedQueued)
            Assert.Equal(DicomStudyDate.Normalize(date), Assert.Single(database.GetRecent(10)).StudyDate);
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData("2026.09.01", true)]
    [InlineData("20260901", true)]
    [InlineData("2026.08.31", false)]
    [InlineData("2026.09.02", false)]
    [InlineData("2026.02.30", false)]
    [InlineData("20269999", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ExistingStoredDatesUseSameRulesInQueueAndHistory(string? date, bool expectedInRange)
    {
        var dbPath = Path.Combine(_directory, "queue.db");
        var database = new Database(dbPath);
        var path = Path.Combine(_directory, "stored.dcm");
        File.WriteAllBytes(path, [1, 2]);
        var pacs = new PacsSettings { Id = "pacs" };
        var target = new PacsSettings { Id = "new-pacs" };
        database.Enqueue("key", path, "1.2.3", null, "folder", [pacs], new DicomMetadata { StudyDate = date });
        // Simulate the unnormalized values persisted by older application versions.
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE queue_items SET study_date=$date;";
            command.Parameters.AddWithValue("$date", (object?)date ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        var folder = new WatchFolderSettings { Id = "folder", SendStudiesFromDate = Cutoff };
        database.ApplyStudyDateFilters([folder]);
        var expectedAtCutoff = DicomStudyDate.TryParse(date, out var parsed) && parsed >= Cutoff;
        Assert.Equal(expectedAtCutoff, database.GetNextReadyDelivery() is not null);

        folder.SendStudiesFromDate = null;
        database.ApplyStudyDateFilters([folder]);
        Assert.NotNull(database.GetNextReadyDelivery());
        Assert.Equal(expectedInRange ? 1 : 0,
            database.CountHistoricalStudies(null, Cutoff, Cutoff, null, target.Id));
        Assert.Equal(expectedInRange ? 1 : 0,
            database.EnqueueHistoricalDeliveries(null, Cutoff, Cutoff, null, target));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
