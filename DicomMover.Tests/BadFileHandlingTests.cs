using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Xunit;

namespace DicomMover.Tests;

public sealed class BadFileHandlingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DicomMoverBadTests-" + Guid.NewGuid().ToString("N"));

    public BadFileHandlingTests()
    {
        Directory.CreateDirectory(_dir);
    }

    private (AppSettings Settings, Database Database, AppLogger Logger, WatchFolderSettings Folder, PacsSettings Pacs, FolderMonitor Monitor) CreateContext()
    {
        var dbPath = Path.Combine(_dir, $"test-{Guid.NewGuid():N}.db");
        var logPath = Path.Combine(_dir, $"test-{Guid.NewGuid():N}.log");
        var database = new Database(dbPath);
        var logger = new AppLogger(logPath);
        var pacs = new PacsSettings { Id = "pacs-1", Name = "Test PACS", Enabled = true };
        var folder = new WatchFolderSettings
        {
            Id = "folder-1",
            Name = "Test Folder",
            Path = Path.Combine(_dir, $"watch-{Guid.NewGuid():N}"),
            PacsIds = [pacs.Id],
            Enabled = true
        };
        Directory.CreateDirectory(folder.Path);
        var settings = new AppSettings
        {
            DatabaseFile = dbPath,
            LogFile = logPath,
            WatchFolders = [folder],
            PacsServers = [pacs],
            FileStableSeconds = 0 // Instant stability for tests
        };
        var monitor = new FolderMonitor(settings, database, logger);
        return (settings, database, logger, folder, pacs, monitor);
    }

    private string CreateValidDicom(string folderPath, string fileName)
    {
        var path = Path.Combine(folderPath, fileName);
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, "Test^Patient" },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([1, 2, 3, 4]))
        };
        new DicomFile(dataset).Save(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    private string CreateCorruptedDicom(string folderPath, string fileName)
    {
        var path = Path.Combine(folderPath, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    [Fact]
    public void CorruptedDicom_RequiresThreeConsecutiveCycles_ToMoveToBad()
    {
        var (_, _, _, folder, _, monitor) = CreateContext();
        var corruptFile = CreateCorruptedDicom(folder.Path, "corrupt.dcm");

        // Cycle 1: File is inspected for the first time. It must NOT be moved to BAD yet!
        monitor.Scan(folder);

        Assert.True(File.Exists(corruptFile), "File should not be moved to BAD on the first scan cycle.");
        var badDir = Path.Combine(folder.Path, "BAD");
        Assert.False(Directory.Exists(badDir) && Directory.EnumerateFiles(badDir).Any(), "BAD folder should not contain files after cycle 1.");

        // Cycle 2: File remains corrupt with unchanged size and mtime. Still cautious (requires 3).
        monitor.Scan(folder);
        Assert.True(File.Exists(corruptFile), "File should not be moved to BAD on the second scan cycle.");

        // Cycle 3: 3 confirmed cycles -> Now it should be moved to BAD
        monitor.Scan(folder);

        Assert.False(File.Exists(corruptFile), "File should have been moved from its original location after cycle 3.");
        Assert.True(Directory.Exists(badDir), "BAD folder should exist.");
        Assert.Single(Directory.EnumerateFiles(badDir, "corrupt*.dcm"));
    }

    [Fact]
    public void FileChangingBetweenScans_IsNotMovedToBad()
    {
        var (_, _, _, folder, _, monitor) = CreateContext();
        var path = CreateCorruptedDicom(folder.Path, "growing.dcm");

        // Cycle 1
        monitor.Scan(folder);
        Assert.True(File.Exists(path));

        // Simulate file modification between scans
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(new byte[] { 11, 12, 13 });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        // Cycle 2: Modified, so conditions for BAD (stable size/mtime across 2 cycles) are not met
        monitor.Scan(folder);
        Assert.True(File.Exists(path), "Modified file must not be moved to BAD.");
    }

    [Fact]
    public void SuccessfullyReadAfterTemporaryError_ClearsSuspiciousState()
    {
        var (_, db, _, folder, _, monitor) = CreateContext();
        var path = Path.Combine(folder.Path, "recovering.dcm");
        File.WriteAllBytes(path, [0x00, 0x01]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

        // Cycle 1: Corrupt
        monitor.Scan(folder);
        Assert.True(File.Exists(path));

        // Now replace with valid DICOM before Cycle 2
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, "1.2.3.4.5" },
            { DicomTag.StudyInstanceUID, "1.2.3.4" },
            { DicomTag.SeriesInstanceUID, "1.2.3" },
            { DicomTag.PatientName, "Valid^Patient" },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([1, 2, 3, 4]))
        };
        new DicomFile(dataset).Save(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

        // Cycle 2: Read succeeds -> Enqueued, not moved to BAD
        monitor.Scan(folder);

        Assert.True(File.Exists(path), "Recovered file should remain in watch folder.");
        Assert.NotNull(db.GetNextReadyDelivery());
    }

    [Fact]
    public void ExclusivelyLockedValidDicom_IsNotMovedToBad()
    {
        var (_, _, _, folder, _, monitor) = CreateContext();
        var path = CreateValidDicom(folder.Path, "locked.dcm");

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Scanning while file is locked
            monitor.Scan(folder);

            Assert.True(File.Exists(path), "Locked file must not be moved to BAD.");
            var badDir = Path.Combine(folder.Path, "BAD");
            Assert.False(Directory.Exists(badDir) && Directory.EnumerateFiles(badDir).Any());
        }
    }

    [Fact]
    public void InspectDicomFile_ClassifiesLockedFile_AsTemporarilyInaccessible()
    {
        var (_, _, _, folder, _, _) = CreateContext();
        var path = CreateValidDicom(folder.Path, "locked-inspect.dcm");

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var inspection = FolderMonitor.InspectDicomFile(path);
            Assert.Equal(DicomFileInspectionStatus.TemporarilyInaccessible, inspection.Status);
        }
    }

    [Fact]
    public void InspectDicomFile_ClassifiesMissingFile_AsNetworkOrFileError()
    {
        var inspection = FolderMonitor.InspectDicomFile(@"C:\NonExistentFolder\nonexistent.dcm");
        Assert.Equal(DicomFileInspectionStatus.NetworkOrFileError, inspection.Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
