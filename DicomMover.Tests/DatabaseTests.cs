using DicomMover.Models;
using DicomMover.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DicomMover.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DicomMoverTests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "queue.db");

    [Fact]
    public void InitializesAndMigratesDatabaseWithoutArchivedColumn()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE queue_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_key TEXT NOT NULL UNIQUE,
                    file_path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    discovered_at_utc TEXT NOT NULL,
                    last_attempt_at_utc TEXT NULL,
                    next_attempt_at_utc TEXT NULL,
                    sent_at_utc TEXT NULL,
                    sop_instance_uid TEXT NULL,
                    pacs_status TEXT NULL,
                    last_error TEXT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var database = new Database(DatabasePath);

        Assert.Empty(database.GetRecent(10));
        Assert.Equal(0, database.ArchiveSentHistory());
    }

    [Fact]
    public void RestoresSendingItemAfterRestart()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("key", "file.dcm", "1.2.3");
        var item = Assert.Single(database.GetRecent(10));
        database.MarkSending(item.Id);

        var reopened = new Database(DatabasePath);
        var restored = Assert.Single(reopened.GetRecent(10));

        Assert.Equal(QueueStatus.Failed, restored.Status);
        Assert.NotNull(reopened.GetNextReady());
    }

    [Fact]
    public void DetectsDuplicateSopInstanceUid()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("first", "first.dcm", "1.2.840.1", "Иванов Иван Иванович");

        Assert.True(database.ContainsSopInstanceUid("1.2.840.1"));
        Assert.False(database.ContainsSopInstanceUid("1.2.840.2"));
        Assert.Equal(
            "Иванов Иван Иванович",
            Assert.Single(database.GetRecent(10)).PatientName);
    }

    [Fact]
    public void ExhaustedItemIsNotAutomaticallyReady()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("key", "file.dcm", "1.2.3");
        var item = Assert.Single(database.GetRecent(10));
        database.MarkSending(item.Id);
        database.MarkFailed(item.Id, "failure", null, attemptsExhausted: true);

        Assert.Null(database.GetNextReady());
        Assert.Equal(QueueStatus.Exhausted, Assert.Single(database.GetRecent(10)).Status);
    }

    [Fact]
    public void HidesSentItemsOlderThanUiCutoffButKeepsThemInDatabase()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("key", "file.dcm", "1.2.3");
        var item = Assert.Single(database.GetRecent(10));
        database.MarkSent(item.Id, "1.2.3", "Success");

        Assert.Empty(database.GetRecent(10, DateTime.UtcNow.AddMinutes(1)));
        Assert.Single(database.GetRecent(10));
    }

    [Fact]
    public void TracksEachPacsDeliveryIndependently()
    {
        var database = new Database(DatabasePath);
        var first = new PacsSettings { Id = "pacs-1", Name = "Основной" };
        var second = new PacsSettings { Id = "pacs-2", Name = "Резервный" };
        database.Enqueue("key", "file.dcm", "1.2.3", "Пациент", "folder", [first, second]);

        var firstDelivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        database.MarkDeliverySending(firstDelivery);
        database.MarkDeliverySent(firstDelivery, "Success");

        var secondDelivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        Assert.NotEqual(firstDelivery.PacsId, secondDelivery.PacsId);
        Assert.False(database.AreAllDeliveriesSent(secondDelivery.QueueItemId));
        database.MarkDeliverySending(secondDelivery);
        database.MarkDeliverySent(secondDelivery, "Success");

        Assert.True(database.AreAllDeliveriesSent(secondDelivery.QueueItemId));
        Assert.Equal(QueueStatus.Sent, Assert.Single(database.GetRecent(10)).Status);
    }

    [Fact]
    public void CanHideAllStatusesWithoutDeletingQueueData()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("pending", "pending.dcm", "1.2.3");
        database.Enqueue("failed", "failed.dcm", "1.2.4");
        var failed = database.GetRecent(10).Single(item => item.FileKey == "failed");
        database.MarkFailed(failed.Id, "Ошибка", null, attemptsExhausted: true);

        Assert.Equal(2, database.ArchiveAllHistory());
        Assert.Empty(database.GetRecent(10));
        Assert.True(database.Contains("pending"));
        Assert.True(database.Contains("failed"));
    }

    [Fact]
    public void PacsOutageDoesNotConsumeFileAttempt()
    {
        var database = new Database(DatabasePath);
        var pacs = new PacsSettings { Id = "pacs", Name = "PACS" };
        database.Enqueue("key", "file.dcm", "1.2.3", null, "folder", [pacs]);
        var delivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        database.MarkDeliverySending(delivery);
        database.MarkDeliveryUnavailable(delivery, "PACS недоступен", DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(0, Assert.Single(database.GetDeliveryDetails(delivery.QueueItemId)).AttemptCount);
    }

    [Fact]
    public void CleanupKeepsCompactSopUidDeduplicationRecord()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("key", "file.dcm", "1.2.3");
        var item = Assert.Single(database.GetRecent(10));
        database.MarkSent(item.Id, "1.2.3", "Success");
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE queue_items SET sent_at_utc='2000-01-01T00:00:00.0000000Z';";
            command.ExecuteNonQuery();
        }
        database = new Database(DatabasePath); // выполняет миграцию UID в компактный реестр

        Assert.Equal(1, database.CleanupOldRecords(365));
        Assert.Empty(database.GetRecent(10));
        Assert.True(database.ContainsSopInstanceUid("1.2.3"));
    }

    [Fact]
    public void CreatesConsistentDatabaseBackup()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("key", "file.dcm", "1.2.3");
        var backupPath = Path.Combine(_directory, "backup.db");

        database.BackupTo(backupPath);

        Assert.Single(new Database(backupPath).GetRecent(10));
    }

    [Fact]
    public void ReportsDailySummaryAndPassesIntegrityCheck()
    {
        var database = new Database(DatabasePath);
        database.Enqueue("key", "file.dcm", "1.2.3");
        var item = Assert.Single(database.GetRecent(10));
        database.MarkSent(item.Id, "1.2.3", "Success");

        var summary = database.GetDailySummary(DateTime.Now);

        Assert.Equal(1, summary.Found);
        Assert.Equal(1, summary.Sent);
        Assert.Equal(0, summary.WithErrors);
        Assert.Equal("ok", database.CheckIntegrityAndOptimize());
    }

    [Fact]
    public void StudyDateFilterHidesExistingQueueAndCanBeRelaxed()
    {
        var database = new Database(DatabasePath);
        var pacs = new PacsSettings { Id = "pacs", Name = "PACS" };
        var folder = new WatchFolderSettings
        {
            Id = "folder", Name = "Folder", Path = _directory, PacsIds = [pacs.Id],
            SendStudiesFromDate = new DateTime(2026, 8, 2)
        };
        database.Enqueue("old", "old.dcm", "1.2.3", null, folder.Id, [pacs], new DicomMetadata { SopInstanceUid = "1.2.3", StudyDate = "20250101" });

        database.ApplyStudyDateFilters([folder]);

        Assert.Empty(database.GetRecent(10));
        Assert.Null(database.GetNextReadyDelivery());

        folder.SendStudiesFromDate = new DateTime(2024, 1, 1);
        database.ApplyStudyDateFilters([folder]);
        Assert.Single(database.GetRecent(10));
        Assert.NotNull(database.GetNextReadyDelivery());
    }

    [Fact]
    public void RetryDeliveryReportsWhetherAlreadySent()
    {
        var database = new Database(DatabasePath);
        var pacs = new PacsSettings { Id = "pacs", Name = "PACS" };
        database.Enqueue("key", "file.dcm", "1.2.3", null, "folder", [pacs]);
        var delivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());

        Assert.True(database.RetryDelivery(delivery.Id) > 0);
        delivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        database.MarkDeliverySending(delivery);
        database.MarkDeliverySent(delivery, "Success");

        Assert.Equal(0, database.RetryDelivery(delivery.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
