using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using Xunit;

namespace DicomMover.Tests;

public sealed class TempFileTranscoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TempTranscoderTests-" + Guid.NewGuid().ToString("N"));

    public TempFileTranscoderTests()
    {
        Directory.CreateDirectory(_dir);
        TempFileManager.TempDirectory = Path.Combine(_dir, "Temp");
    }

    private string CreateDicomFile(string fileName, byte[] pixelData, string patientName = "Иванов^Иван")
    {
        var path = Path.Combine(_dir, fileName);
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 100" },
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, patientName },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer(pixelData))
        };
        new DicomFile(dataset).Save(path);
        return path;
    }

    [Fact]
    public void Transcode_SavesToDiskTempFile_AndPreservesPixelData()
    {
        var pixelData = new byte[1024 * 100]; // 100 KB
        pixelData[0] = 0x42;
        pixelData[^1] = 0x99;

        var sourcePath = CreateDicomFile("source.dcm", pixelData, "Петров^Петр");
        var transcoder = new DicomTextTranscoder();
        var rule = new EncodingRule
        {
            Name = "CP1251 to UTF-8",
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        };

        var result = transcoder.BuildTransformedDicom(sourcePath, rule);

        Assert.NotNull(result.TempFilePath);
        Assert.True(File.Exists(result.TempFilePath), "Temp file must exist on disk after BuildTransformedDicom.");

        // Check that temp file name doesn't contain patient name
        var tempFileName = Path.GetFileName(result.TempFilePath);
        Assert.DoesNotContain("Петров", tempFileName);
        Assert.DoesNotContain("Петр", tempFileName);

        // Check reopened DICOM from temp file
        var reopened = DicomFile.Open(result.TempFilePath);
        var pixelElement = reopened.Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData);
        Assert.NotNull(pixelElement);
        var readBytes = pixelElement.Buffer.Data;
        Assert.Equal(pixelData.Length, readBytes.Length);
        Assert.Equal(0x42, readBytes[0]);
        Assert.Equal(0x99, readBytes[^1]);

        // SHA-256 verification of full pixel data integrity before and after transcoding
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var sourceHash = sha256.ComputeHash(pixelData);
        var transcodedHash = sha256.ComputeHash(readBytes);
        Assert.Equal(sourceHash, transcodedHash);

        Assert.Equal("ISO_IR 192", reopened.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, string.Empty));

        // Clean up
        File.Delete(result.TempFilePath);
    }

    [Fact]
    public async Task SendAsync_DeletesTempFileInFinally_AfterSending()
    {
        var dbPath = Path.Combine(_dir, "queue.db");
        var logPath = Path.Combine(_dir, "app.log");
        var db = new Database(dbPath);
        var logger = new AppLogger(logPath);
        var pacs = new PacsSettings { Id = "pacs-1", Name = "PACS 1", Enabled = true };
        var folder = new WatchFolderSettings { Id = "folder-1", Name = "Folder 1", Path = _dir, PacsIds = [pacs.Id], Enabled = true };

        var rule = new EncodingRule
        {
            Name = "UTF8 rule",
            SourceFolderId = folder.Id,
            DestinationPacsId = pacs.Id,
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        };

        var settings = new AppSettings
        {
            DatabaseFile = dbPath,
            LogFile = logPath,
            WatchFolders = [folder],
            PacsServers = [pacs],
            EncodingRules = [rule]
        };

        var sourcePath = CreateDicomFile("file_to_send.dcm", [1, 2, 3, 4], "Сидоров^Сидор");
        db.Enqueue("key-send", sourcePath, "1.2.3.4", "Сидоров Сидор", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        string? capturedTempPath = null;
        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (p, req, token) =>
            {
                Assert.NotNull(req.File);
                capturedTempPath = req.File.File.Name;
                Assert.True(File.Exists(capturedTempPath), "Temp file must exist while sending.");
                return Task.FromResult<DicomStatus?>(DicomStatus.Success);
            }
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        Assert.NotNull(capturedTempPath);
        Assert.False(File.Exists(capturedTempPath), "Temp file must be deleted in finally after SendAsync.");
    }

    [Fact]
    public void CleanStaleTempFiles_DeletesOrphanedFiles_AndPreservesCurrentProcessFiles()
    {
        var tempDir = TempFileManager.GetTempDirectory();
        var staleFile = Path.Combine(tempDir, "tx_99999999_deadprocess.dcm");
        File.WriteAllText(staleFile, "stale");

        var currentFile = Path.Combine(tempDir, $"tx_{Environment.ProcessId}_currentprocess.dcm");
        File.WriteAllText(currentFile, "current");

        TempFileManager.CleanStaleTempFiles();

        Assert.False(File.Exists(staleFile), "Stale file from non-existent PID must be cleaned.");
        Assert.True(File.Exists(currentFile), "File from current active process must be preserved.");

        File.Delete(currentFile);
    }

    [Fact]
    public void Transcode_LargeDicom_DoesNotDuplicatePixelDataInMemoryStream()
    {
        // 5 MB synthetic DICOM
        var pixelData = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(pixelData);

        var path = CreateDicomFile("large.dcm", pixelData, "Тестов^Тест");
        var transcoder = new DicomTextTranscoder();
        var rule = new EncodingRule
        {
            Name = "Rule",
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memBefore = GC.GetTotalMemory(true);

        var result = transcoder.BuildTransformedDicom(path, rule);

        var memAfter = GC.GetTotalMemory(false);
        var allocated = memAfter - memBefore;

        // With disk temp file, MemoryStream(5 MB) is eliminated during verification!
        Assert.NotNull(result.TempFilePath);
        Assert.True(File.Exists(result.TempFilePath));

        File.Delete(result.TempFilePath);
    }

    [Fact]
    public async Task InsufficientDiskSpace_ReportsOperationalProblem_WithoutSending()
    {
        var dbPath = Path.Combine(_dir, "diskspace.db");
        var logPath = Path.Combine(_dir, "app.log");
        var db = new Database(dbPath);
        var logger = new AppLogger(logPath);
        var pacs = new PacsSettings { Id = "pacs-disk", Name = "PACS Disk", Enabled = true };
        var folder = new WatchFolderSettings { Id = "folder-disk", Name = "Folder Disk", Path = _dir, PacsIds = [pacs.Id], Enabled = true };

        var settings = new AppSettings
        {
            DatabaseFile = dbPath,
            LogFile = logPath,
            WatchFolders = [folder],
            PacsServers = [pacs],
            EncodingRules = []
        };

        var sourcePath = CreateDicomFile("disk_test.dcm", [1, 2, 3, 4], "Тест");
        db.Enqueue("key-disk", sourcePath, "1.2.3.999", "Тест", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (_, _, _) => throw new InsufficientDiskSpaceException("C:", 10L * 1024 * 1024, 200L * 1024 * 1024)
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        // 1. Delivery state transition: Pending -> Failed (Ошибка), NOT Unavailable (PACS недоступен)
        var deliveryDetails = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Ошибка", deliveryDetails.Status);
        Assert.Contains("Недостаточно свободного места", deliveryDetails.LastError);

        // 2. PACS remains available! (Must NOT call MarkPacsUnavailable)
        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.NotEqual(false, pacsStatus.Available);

        // 3. Local problem DiskSpace is reported, NOT PacsConnection
        var problems = db.GetActiveProblems();
        Assert.Contains(problems, p => p.Kind == "DiskSpace" && p.Key == "diskspace:C:");
        Assert.DoesNotContain(problems, p => p.Kind == "PacsConnection");
    }

    [Fact]
    public void Transcode_ReopensDicom_AndVerifiesTagsAndNoReplacementCharacters()
    {
        var sourcePath = CreateDicomFile("verify_check.dcm", [1, 2, 3, 4], "Петров^Иван");
        var transcoder = new DicomTextTranscoder();
        var rule = new EncodingRule
        {
            Name = "CP1251 to UTF-8",
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        };

        var result = transcoder.BuildTransformedDicom(sourcePath, rule);
        Assert.NotNull(result.TempFilePath);

        var verified = DicomFile.Open(result.TempFilePath);
        Assert.Equal("ISO_IR 192", verified.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, ""));
        Assert.True(verified.Dataset.Contains(DicomTag.PixelData));

        var patientName = verified.Dataset.GetString(DicomTag.PatientName);
        Assert.DoesNotContain('\uFFFD', patientName);

        File.Delete(result.TempFilePath);
    }

    public void Dispose()
    {
        TempFileManager.ResetDefaultTempDirectory();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
