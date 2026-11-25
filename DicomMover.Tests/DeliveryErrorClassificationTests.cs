using System.Net.Sockets;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Xunit;

namespace DicomMover.Tests;

public sealed class DeliveryErrorClassificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DicomMoverErrorTests-" + Guid.NewGuid().ToString("N"));

    public DeliveryErrorClassificationTests()
    {
        Directory.CreateDirectory(_dir);
    }

    private (AppSettings Settings, Database Database, AppLogger Logger, WatchFolderSettings Folder, PacsSettings Pacs) CreateContext()
    {
        var dbPath = Path.Combine(_dir, $"test-{Guid.NewGuid():N}.db");
        var logPath = Path.Combine(_dir, $"test-{Guid.NewGuid():N}.log");
        var database = new Database(dbPath);
        var logger = new AppLogger(logPath);
        var pacs = new PacsSettings
        {
            Id = "pacs-1",
            Name = "Test PACS",
            IpAddress = "127.0.0.1",
            Port = 104,
            ConnectionTimeoutSeconds = 1,
            SendTimeoutSeconds = 2
        };
        var folder = new WatchFolderSettings
        {
            Id = "folder-1",
            Name = "Test Folder",
            Path = Path.Combine(_dir, "watch"),
            PacsIds = [pacs.Id]
        };
        Directory.CreateDirectory(folder.Path);
        var settings = new AppSettings
        {
            DatabaseFile = dbPath,
            LogFile = logPath,
            WatchFolders = [folder],
            PacsServers = [pacs],
            MaxSendAttempts = 3
        };
        return (settings, database, logger, folder, pacs);
    }

    private string CreateDicomFile(string folderPath, string fileName = "sample.dcm")
    {
        var path = Path.Combine(folderPath, fileName);
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, "Test^Patient" },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([0, 1, 2, 3]))
        };
        new DicomFile(dataset).Save(path);
        return path;
    }

    [Fact]
    public async Task CStoreTimeout_MarksDeliveryFailedWithBackoff_AndDoesNotMarkPacsUnavailable()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var filePath = CreateDicomFile(folder.Path);
        db.Enqueue("key-1", filePath, "1.2.3", "Test Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (p, req, token) => throw new OperationCanceledException("Operation timed out")
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Ошибка", details.Status); // Failed -> "Ошибка" in GetDeliveryDetails
        Assert.Equal(1, details.AttemptCount);
        Assert.Contains("Таймаут", details.LastError);

        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.NotEqual(false, pacsStatus.Available);
    }

    [Fact]
    public async Task MissingSourceFile_StopsRetriesAndReportsProblem_AndDoesNotMarkPacsUnavailable()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var missingPath = Path.Combine(folder.Path, "non-existent.dcm");
        db.Enqueue("key-missing", missingPath, "1.2.3.missing", "Missing Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true)
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Лимит попыток", details.Status); // Exhausted -> "Лимит попыток"

        var problems = db.GetActiveProblems();
        Assert.Contains(problems, p => p.Kind == "FileNotFound" || p.Details.Contains("не найден"));

        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.NotEqual(false, pacsStatus.Available);
    }

    [Fact]
    public async Task TransientFileLock_RetriesWithBackoff_AndDoesNotMarkPacsUnavailable()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var filePath = CreateDicomFile(folder.Path);
        db.Enqueue("key-lock", filePath, "1.2.3.lock", "Lock Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (p, req, token) => throw new IOException("The process cannot access the file because it is being used by another process.")
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Ошибка", details.Status);
        Assert.Equal(1, details.AttemptCount);
        Assert.Contains("The process cannot access the file", details.LastError);

        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.NotEqual(false, pacsStatus.Available);
    }

    [Fact]
    public async Task RealNetworkConnectionError_MarksDeliveryUnavailable_AndMarksPacsUnavailable()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var filePath = CreateDicomFile(folder.Path);
        db.Enqueue("key-net", filePath, "1.2.3.net", "Net Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (p, req, token) => throw new DicomNetworkException("Connection reset by peer")
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("PACS недоступен", details.Status);

        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.False(pacsStatus.Available);
    }

    [Fact]
    public async Task PacsRejection_MarksDeliveryFailed_AndPacsRemainsAvailable()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var filePath = CreateDicomFile(folder.Path);
        db.Enqueue("key-rej", filePath, "1.2.3.rej", "Rej Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (p, req, token) => Task.FromResult<DicomStatus?>(DicomStatus.ProcessingFailure)
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Ошибка", details.Status);
        Assert.Equal(1, details.AttemptCount);

        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.NotEqual(false, pacsStatus.Available);
    }

    [Fact]
    public async Task PacsWarning_MarksDeliverySent_AndPacsRemainsAvailable()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var filePath = CreateDicomFile(folder.Path);
        db.Enqueue("key-warn", filePath, "1.2.3.warn", "Warn Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (p, req, token) => Task.FromResult<DicomStatus?>(DicomStatus.StorageCoercionOfDataElements)
        };

        await monitor.SendAsync(delivery, CancellationToken.None);

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Отправлено", details.Status);

        var statuses = monitor.GetRuntimeStatuses();
        var pacsStatus = statuses.First(s => s.Id == pacs.Id);
        Assert.NotEqual(false, pacsStatus.Available);
    }

    [Fact]
    public async Task AppShutdown_DoesNotConsumeAttempt_AndRestoresPending()
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        var filePath = CreateDicomFile(folder.Path);
        db.Enqueue("key-shutdown", filePath, "1.2.3.shutdown", "Shutdown Patient", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var monitor = new FolderMonitor(settings, db, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.SendAsync(delivery, cts.Token));

        var details = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal("Ожидает", details.Status);
        Assert.Equal(0, details.AttemptCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData(true, "duplicate", null, true)]
    [InlineData(false, "duplicate", null, false)]
    [InlineData(true, "failure", "Duplicate SOP Instance", true)]
    [InlineData(true, "failure", "Duplicate study", true)]
    [InlineData(true, "failure", "Disk full", false)]
    [InlineData(true, "failure", null, false)]
    [InlineData(true, "invocation", "Duplicate SOP Instance", false)]
    public async Task DuplicateOptionOnlyCompletesExplicitDuplicatesAndPreservesSource(
        bool enabled, string responseKind, string? comment, bool expectedSkipped)
    {
        var (settings, db, logger, folder, pacs) = CreateContext();
        settings.SkipPacsDuplicates = enabled;
        folder.PostSendAction = PostSendAction.Delete;
        var path = CreateDicomFile(folder.Path);
        db.Enqueue("duplicate", path, "1.2.3", null, folder.Id, [pacs]);
        db.Enqueue("next", path, "1.2.4", null, folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;
        var status = responseKind switch
        {
            "duplicate" => DicomStatus.DuplicateSOPInstance,
            "invocation" => DicomStatus.DuplicateInvocation,
            _ => DicomStatus.ProcessingFailure
        };
        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (_, request, _) =>
            {
                var response = new DicomCStoreResponse(request, status);
                if (comment is not null) response.Command.AddOrUpdate(DicomTag.ErrorComment, comment);
                request.OnResponseReceived(request, response);
                return Task.FromResult<DicomStatus?>(status);
            }
        };
        await monitor.SendAsync(delivery, CancellationToken.None);
        var detail = Assert.Single(db.GetDeliveryDetails(delivery.QueueItemId));
        Assert.Equal(expectedSkipped ? "Дубликат пропущен" : "Ошибка", detail.Status);
        Assert.Equal(expectedSkipped, db.AreAllDeliveriesSent(delivery.QueueItemId));
        Assert.True(File.Exists(path));
        Assert.NotEqual(delivery.Id, db.GetNextReadyDelivery()!.Id);
        if (expectedSkipped)
        {
            Assert.Null(db.GetItem(delivery.QueueItemId)!.LastError);
            Assert.StartsWith(PacsDuplicateClassifier.SkippedPrefix, detail.PacsStatus);
            // A success at another PACS must not delete a source rejected as a duplicate elsewhere.
            var secondPacs = pacs.Copy();
            secondPacs.Id = "second";
            settings.PacsServers.Add(secondPacs);
            var mixedDb = new Database(Path.Combine(_dir, "mixed.db"));
            mixedDb.Enqueue("mixed", path, "1.2.5", null, folder.Id, [pacs, secondPacs]);
            var mixedMonitor = new FolderMonitor(settings, mixedDb, logger)
            {
                PacsEchoChecker = (_, _) => Task.FromResult(true),
                DicomSender = (target, _, _) => Task.FromResult<DicomStatus?>(
                    target.Id == pacs.Id ? DicomStatus.DuplicateSOPInstance : DicomStatus.Success)
            };
            await mixedMonitor.SendAsync(mixedDb.GetNextReadyDelivery()!, CancellationToken.None);
            await mixedMonitor.SendAsync(mixedDb.GetNextReadyDelivery()!, CancellationToken.None);
            Assert.Null(mixedDb.GetNextReadyDelivery());
            Assert.True(File.Exists(path));
            // Existing duplicate state is persisted, including after reopening SQLite.
            Assert.True(new Database(settings.DatabaseFile).HasSkippedDuplicateDeliveries(delivery.QueueItemId));
        }
    }
}
