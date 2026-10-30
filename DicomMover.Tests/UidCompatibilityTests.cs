using System.Security.Cryptography;
using System.Text;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DicomMover.Tests;

public sealed class UidCompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DicomMoverUidTests-" + Guid.NewGuid().ToString("N"));
    public UidCompatibilityTests() => Directory.CreateDirectory(_dir);

    private string Create(string name, byte pixel, bool validUids = false)
    {
        var path = Path.Combine(_dir, "watch", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.ComputedRadiographyImageStorage },
            { DicomTag.SOPInstanceUID, validUids ? "1.2.3.4.5" : DicomUID.ComputedRadiographyImageStorage.UID },
            { DicomTag.StudyInstanceUID, "1.2.3.4.6" },
            { DicomTag.SeriesInstanceUID, "1.2.3.4.7" },
            { DicomTag.StudyDate, "20260901" },
            { DicomTag.PatientName, "Test^Patient" },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([pixel, 0, pixel, 0]))
        };
        new DicomFile(dataset).Save(path);
        if (!validUids)
        {
            PatchValue(path, [0x20, 0, 0x0d, 0, 0x55, 0x49], "0", 0);
            PatchValue(path, [0x20, 0, 0x0e, 0, 0x55, 0x49], "0", 0);
            PatchValue(path, [0x08, 0, 0x20, 0, 0x44, 0x41], "2026.09.01", 0x20);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    private static void PatchValue(string path, byte[] marker, string value, byte padding)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.AsSpan().IndexOf(marker);
        Assert.True(offset >= 0);
        var oldLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6, 2));
        var data = Encoding.ASCII.GetBytes(value);
        var newLength = (data.Length + 1) & ~1;
        using var output = new MemoryStream();
        output.Write(bytes.AsSpan(0, offset + 6));
        output.WriteByte((byte)newLength);
        output.WriteByte(0);
        output.Write(data);
        if (data.Length != newLength) output.WriteByte(padding);
        output.Write(bytes.AsSpan(offset + 8 + oldLength));
        File.WriteAllBytes(path, output.ToArray());
    }

    private (Database Db, WatchFolderSettings Folder, FolderMonitor Monitor) Context(bool ignore, bool repair)
    {
        var pacs = new PacsSettings { Id = "pacs" };
        var folder = new WatchFolderSettings { Id = "folder", Path = Path.Combine(_dir, "watch"), PacsIds = [pacs.Id], IgnoreSopInstanceUidDuplicates = ignore, RepairInvalidDicomUids = repair };
        var db = new Database(Path.Combine(_dir, "queue.db"));
        var settings = new AppSettings { WatchFolders = [folder], PacsServers = [pacs], FileStableSeconds = 0 };
        var monitor = new FolderMonitor(settings, db, new AppLogger(Path.Combine(_dir, "app.log"))) { PacsEchoChecker = (_, _) => Task.FromResult(true) };
        return (db, folder, monitor);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 2)]
    public void FolderModeDistinguishesImagesButNotExactCopies(bool ignore, bool repair, int expected)
    {
        var first = Create("a.dcm", 10);
        Create("b.dcm", 20);
        File.Copy(first, Path.Combine(_dir, "watch", "copy.dcm"));
        var (db, folder, monitor) = Context(ignore, repair);
        monitor.Scan(folder);
        monitor.Scan(folder);
        Assert.Equal(expected, db.GetRecent(10).Count);
        // New monitor simulates a restart; identity is persisted, not kept only in memory.
        var restarted = Context(ignore, repair);
        restarted.Monitor.Scan(restarted.Folder);
        Assert.Equal(expected, restarted.Db.GetRecent(10).Count);
    }

    [Fact]
    public async Task RepairUsesStableUidAndPreservesPixelsAndOriginalFileOnRetry()
    {
        var path = Create("a.dcm", 23);
        var original = File.ReadAllBytes(path);
        var hash = DicomUidCompatibility.HashFile(path);
        var (db, folder, monitor) = Context(true, true);
        monitor.Scan(folder);
        var delivery = db.GetNextReadyDelivery()!;
        DicomFile? sent = null;
        monitor.DicomSender = (_, request, _) => { sent = request.File; return Task.FromResult<DicomStatus?>(DicomStatus.ProcessingFailure); };
        await monitor.SendAsync(delivery, CancellationToken.None);
        Assert.NotNull(sent);
        var firstUid = sent.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
        Assert.Equal(DicomUidCompatibility.GenerateUid(hash, "instance"), firstUid);
        Assert.Equal(delivery.SopInstanceUid, firstUid);
        Assert.Equal(firstUid, sent.FileMetaInfo.MediaStorageSOPInstanceUID.UID);
        Assert.Equal(DicomUID.ComputedRadiographyImageStorage.UID, sent.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID));
        Assert.Equal(DicomUidCompatibility.GenerateUid(hash, "study"), sent.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID));
        Assert.Equal(DicomUidCompatibility.GenerateUid(hash, "series"), sent.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
        Assert.Equal(new byte[] { 23, 0, 23, 0 }, sent.Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data);
        Assert.Equal("20260901", sent.Dataset.GetSingleValue<string>(DicomTag.StudyDate));
        folder.RepairInvalidDicomUids = false; // Queued policy must remain stable.
        db.RetryDelivery(delivery.Id);
        monitor.DicomSender = (_, request, _) => { sent = request.File; return Task.FromResult<DicomStatus?>(DicomStatus.Success); };
        await monitor.SendAsync(db.GetNextReadyDelivery()!, CancellationToken.None);
        Assert.Equal(firstUid, sent.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID));
        Assert.Equal(original, File.ReadAllBytes(path));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "queue.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT original_sop_instance_uid FROM queue_items;";
        Assert.Equal(DicomUID.ComputedRadiographyImageStorage.UID, command.ExecuteScalar());
    }

    [Fact]
    public void ValidUidsRemainUnchangedAndRolesProduceDifferentValues()
    {
        var path = Create("a.dcm", 17, validUids: true);
        var file = DicomFile.Open(path);
        DicomUidCompatibility.Apply(file, DicomUidCompatibility.HashFile(path));
        Assert.Equal("1.2.3.4.5", file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID));
        Assert.Equal("1.2.3.4.6", file.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID));
        Assert.Equal("1.2.3.4.7", file.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
        var uids = new[] { "instance", "study", "series" }.Select(role => DicomUidCompatibility.GenerateUid("ABC", role)).ToArray();
        Assert.Equal(3, uids.Distinct().Count());
        foreach (var uid in uids) Assert.Matches(@"^2\.25\.[0-9]{1,39}$", uid);
    }

    [Fact]
    public void EnablingCompatibilityAdoptsExistingEntryWithoutLosingDeliveryHistory()
    {
        Create("a.dcm", 11);
        var (db, folder, monitor) = Context(false, false);
        monitor.Scan(folder);
        var before = Assert.Single(db.GetRecent(10));
        var delivery = db.GetNextReadyDelivery()!;
        db.MarkDeliverySending(delivery);
        db.MarkDeliveryFailed(delivery, "PACS rejection", DateTime.UtcNow, false);
        folder.IgnoreSopInstanceUidDuplicates = true;
        folder.RepairInvalidDicomUids = true;
        monitor.Scan(folder);
        var after = Assert.Single(db.GetRecent(10));
        Assert.Equal(before.Id, after.Id);
        Assert.StartsWith("2.25.", after.SopInstanceUid);
        Assert.Equal(1, Assert.Single(db.GetDeliveryDetails(before.Id)).AttemptCount);
        Assert.True(db.GetCompatibilityState(before.Id)!.Value.RepairUids);
    }

    [Fact]
    public async Task ModifiedFileIsNotSentWithQueuedIdentity()
    {
        var path = Create("a.dcm", 12);
        var (db, folder, monitor) = Context(true, true);
        monitor.Scan(folder);
        File.AppendAllText(path, "changed");
        var called = false;
        monitor.DicomSender = (_, _, _) => { called = true; return Task.FromResult<DicomStatus?>(DicomStatus.Success); };
        var delivery = db.GetNextReadyDelivery()!;
        await monitor.SendAsync(delivery, CancellationToken.None);
        Assert.False(called);
        Assert.Contains("SHA-256", Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId)).LastError);
    }

    [Fact]
    public async Task ContentDeduplicationSurvivesQueueRetentionCleanup()
    {
        Create("a.dcm", 13);
        var (db, folder, monitor) = Context(true, false);
        monitor.Scan(folder);
        monitor.DicomSender = (_, _, _) => Task.FromResult<DicomStatus?>(DicomStatus.Success);
        await monitor.SendAsync(db.GetNextReadyDelivery()!, CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "queue.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE queue_items SET sent_at_utc='2000-01-01T00:00:00Z';";
            command.ExecuteNonQuery();
        }
        Assert.Equal(1, db.CleanupOldRecords(365));
        monitor.Scan(folder);
        Assert.Empty(db.GetRecent(10));
    }

    [Fact]
    public void FolderOptionsCopyAndSettingsRoundTrip()
    {
        var settings = new AppSettings { WatchFolders = [new WatchFolderSettings { IgnoreSopInstanceUidDuplicates = true, RepairInvalidDicomUids = true, PacsIds = ["pacs"] }], PacsServers = [new PacsSettings { Id = "pacs" }] };
        var copy = settings.Copy().WatchFolders[0];
        Assert.True(copy.IgnoreSopInstanceUidDuplicates);
        Assert.True(copy.RepairInvalidDicomUids);
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(settings);
        var loaded = Assert.Single(store.Load().WatchFolders);
        Assert.True(loaded.IgnoreSopInstanceUidDuplicates);
        Assert.True(loaded.RepairInvalidDicomUids);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
