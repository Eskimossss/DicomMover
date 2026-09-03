using DicomMover.Models;
using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class HistoricalSendingWizardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HistoryWizardTests-" + Guid.NewGuid().ToString("N"));

    public HistoricalSendingWizardTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void EnqueueHistoricalDeliveries_QueuesOnlyForTargetPacs_AndDoesNotDuplicateExisting()
    {
        var dbPath = Path.Combine(_dir, "hist_test.db");
        var db = new Database(dbPath);

        var pacs1 = new PacsSettings { Id = "pacs-1", Name = "PACS 1" };
        var pacs2 = new PacsSettings { Id = "pacs-2", Name = "New PACS 2" };

        var file1 = Path.Combine(_dir, "1.dcm");
        var file2 = Path.Combine(_dir, "2.dcm");
        File.WriteAllBytes(file1, [1, 2, 3, 4]);
        File.WriteAllBytes(file2, [5, 6, 7, 8]);

        // Item 1: sent to PACS 1
        db.Enqueue("item-1", file1, "1.1.1", "Иванов", "f1", [pacs1]);
        var delivery1 = db.GetNextReadyDelivery()!;
        db.MarkDeliverySent(delivery1, "0000");

        // Item 2: sent to PACS 1
        db.Enqueue("item-2", file2, "1.1.2", "Петров", "f1", [pacs1]);
        var delivery2 = db.GetNextReadyDelivery()!;
        db.MarkDeliverySent(delivery2, "0000");

        // Archive sent items
        db.ArchiveSentHistory();
        Assert.Empty(db.GetRecent(10));

        // Check count for New PACS 2
        var summary = db.CalculateHistoricalStudies(folderId: null, fromDate: null, toDate: null, modality: null, targetPacsId: pacs2.Id);
        Assert.Equal(2, summary.EligibleCount);
        Assert.Equal(8, summary.TotalBytes);
        Assert.Equal(0, summary.MissingFilesCount);

        // Preview count
        var preview = db.PreviewHistoricalStudies(folderId: null, fromDate: null, toDate: null, modality: null, targetPacsId: pacs2.Id);
        Assert.Equal(2, preview.Count);

        // Enqueue to pacs2
        var enqueued = db.EnqueueHistoricalDeliveries(folderId: null, fromDate: null, toDate: null, modality: null, targetPacs: pacs2);
        Assert.Equal(2, enqueued);

        // Verify deliveries for item-1: PACS 1 is Sent, PACS 2 is Pending
        var item1Id = preview.First(x => x.FileKey == "item-1").Id;
        var deliveries1 = db.GetDeliveryDetails(item1Id);
        Assert.Equal(2, deliveries1.Count);

        var d1Pacs1 = deliveries1.Single(d => d.PacsName == "PACS 1");
        Assert.Equal("Отправлено", d1Pacs1.Status);

        var d1Pacs2 = deliveries1.Single(d => d.PacsName == "New PACS 2");
        Assert.Equal("Ожидает", d1Pacs2.Status);

        // Verify item-1 was un-archived (now visible in GetRecent)
        var recent = db.GetRecent(10);
        Assert.Contains(recent, r => r.Id == item1Id);

        // Second run should find 0 and insert 0 (no duplicates!)
        var countAgain = db.CountHistoricalStudies(folderId: null, fromDate: null, toDate: null, modality: null, targetPacsId: pacs2.Id);
        Assert.Equal(0, countAgain);

        var enqueuedAgain = db.EnqueueHistoricalDeliveries(folderId: null, fromDate: null, toDate: null, modality: null, targetPacs: pacs2);
        Assert.Equal(0, enqueuedAgain);
    }

    [Fact]
    public void EnqueueHistoricalDeliveries_FiltersByFolderAndModality_AndChecksFileExistence()
    {
        var dbPath = Path.Combine(_dir, "hist_filter_test.db");
        var db = new Database(dbPath);

        var pacs1 = new PacsSettings { Id = "pacs-1", Name = "PACS 1" };
        var pacsNew = new PacsSettings { Id = "pacs-new", Name = "New PACS" };

        var fileCt = Path.Combine(_dir, "ct.dcm");
        var fileDx = Path.Combine(_dir, "dx.dcm");
        var fileF2 = Path.Combine(_dir, "f2.dcm");
        File.WriteAllBytes(fileCt, [10, 20]);
        File.WriteAllBytes(fileDx, [30, 40]);
        File.WriteAllBytes(fileF2, [50, 60]);

        // Item 1: folder f1, Modality CT
        db.Enqueue("item-ct", fileCt, "1.1.10", "Сидоров", "f1", [pacs1],
            new DicomMetadata { SopInstanceUid = "1.1.10", Modality = "CT", StudyDate = "20230501" });
        // Item 2: folder f1, Modality DX
        db.Enqueue("item-dx", fileDx, "1.1.11", "Кузнецов", "f1", [pacs1],
            new DicomMetadata { SopInstanceUid = "1.1.11", Modality = "DX", StudyDate = "20230502" });
        // Item 3: folder f2, Modality CT
        db.Enqueue("item-f2", fileF2, "1.1.12", "Смирнов", "f2", [pacs1],
            new DicomMetadata { SopInstanceUid = "1.1.12", Modality = "CT", StudyDate = "20230503" });

        // Filter: only folder f1 and Modality CT -> should match exactly 1
        var count = db.CountHistoricalStudies(folderId: "f1", fromDate: null, toDate: null, modality: "CT", targetPacsId: pacsNew.Id);
        Assert.Equal(1, count);

        var enqueued = db.EnqueueHistoricalDeliveries(folderId: "f1", fromDate: null, toDate: null, modality: "CT", targetPacs: pacsNew);
        Assert.Equal(1, enqueued);

        // Distinct modalities in DB
        var modalities = db.GetDistinctModalities();
        Assert.Contains("CT", modalities);
        Assert.Contains("DX", modalities);
    }

    [Fact]
    public void HistoricalStudies_SkipsMissingFiles_AndReportsThem()
    {
        var dbPath = Path.Combine(_dir, "hist_missing_test.db");
        var db = new Database(dbPath);

        var pacs = new PacsSettings { Id = "pacs-missing", Name = "PACS Missing" };
        var realFile = Path.Combine(_dir, "exists.dcm");
        var missingFile = Path.Combine(_dir, "missing.dcm");
        File.WriteAllBytes(realFile, [1, 2, 3]);

        db.Enqueue("item-exists", realFile, "1.1.20", "Иванов", "f1", []);
        db.Enqueue("item-missing", missingFile, "1.1.21", "Пропащий", "f1", []);

        var summary = db.CalculateHistoricalStudies(folderId: null, fromDate: null, toDate: null, modality: null, targetPacsId: pacs.Id);
        Assert.Equal(1, summary.EligibleCount);
        Assert.Equal(1, summary.MissingFilesCount);

        var enqueued = db.EnqueueHistoricalDeliveries(folderId: null, fromDate: null, toDate: null, modality: null, targetPacs: pacs);
        Assert.Equal(1, enqueued);
    }

    [Fact]
    public void HistoricalStudies_Cancellation_AbortsWithoutPartialCorruption()
    {
        var dbPath = Path.Combine(_dir, "hist_cancel_test.db");
        var db = new Database(dbPath);

        var pacs = new PacsSettings { Id = "pacs-cancel", Name = "PACS Cancel" };
        var file = Path.Combine(_dir, "cancel.dcm");
        File.WriteAllBytes(file, [1, 2, 3]);

        db.Enqueue("item-cancel", file, "1.1.30", "Тестов", "f1", []);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        Assert.Throws<OperationCanceledException>(() =>
        {
            db.EnqueueHistoricalDeliveries(folderId: null, fromDate: null, toDate: null, modality: null, targetPacs: pacs, cancellationToken: cts.Token);
        });

        // Deliveries should NOT have been created
        var deliveries = db.GetDeliveryDetails(1);
        Assert.Empty(deliveries);
    }

    [Fact]
    public void HistoricalStudies_CancellationMidOperation_PreservesCreatedDeliveries_AndRerunAvoidsDuplicates()
    {
        var dbPath = Path.Combine(_dir, "hist_cancel_mid.db");
        var db = new Database(dbPath);
        var pacs = new PacsSettings { Id = "pacs-mid", Name = "PACS Mid" };

        const int totalCount = 10;
        for (var i = 1; i <= totalCount; i++)
        {
            var file = Path.Combine(_dir, $"mid_{i}.dcm");
            File.WriteAllBytes(file, [1, 2, 3]);
            db.Enqueue($"key-{i}", file, $"1.2.840.{i}", $"Patient {i}", "f1", []);
        }

        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<int>(enqueuedSoFar =>
        {
            if (enqueuedSoFar >= 4)
            {
                cts.Cancel(); // Cancel mid-operation synchronously after 4 items
            }
        });

        Assert.Throws<OperationCanceledException>(() =>
        {
            db.EnqueueHistoricalDeliveries(
                folderId: null,
                fromDate: null,
                toDate: null,
                modality: null,
                targetPacs: pacs,
                cancellationToken: cts.Token,
                progress: progress);
        });

        // 1. Verify that the 4 deliveries created before cancellation ARE preserved in SQLite!
        var countFirst = db.CountHistoricalStudies(null, null, null, null, pacs.Id);
        Assert.True(countFirst < totalCount, "Some items must have been enqueued and therefore removed from historical candidates.");
        var remainingCandidates = totalCount - 4;
        Assert.Equal(remainingCandidates, countFirst);

        // 2. Re-run wizard on the same PACS: must find and enqueue ONLY remaining items, with zero duplicates!
        var secondEnqueued = db.EnqueueHistoricalDeliveries(
            folderId: null,
            fromDate: null,
            toDate: null,
            modality: null,
            targetPacs: pacs);

        Assert.Equal(remainingCandidates, secondEnqueued);

        // 3. Now 0 candidates remain
        var finalCandidates = db.CountHistoricalStudies(null, null, null, null, pacs.Id);
        Assert.Equal(0, finalCandidates);

        // 4. Verify each item has exactly 1 delivery
        for (var i = 1; i <= totalCount; i++)
        {
            var itemDeliveries = db.GetDeliveryDetails(i);
            Assert.Single(itemDeliveries);
            Assert.Equal("PACS Mid", itemDeliveries[0].PacsName);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
