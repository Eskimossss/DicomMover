using DicomMover.Models;
using Microsoft.Data.Sqlite;

namespace DicomMover.Services;

public sealed class Database
{
    private readonly string _connectionString;

    public Database(string databaseFile)
    {
        var path = Path.GetFullPath(databaseFile);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        CreateMigrationBackupIfNeeded(path);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();

        Initialize();
    }

    public bool Contains(string fileKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM queue_items WHERE file_key = $key);";
        command.Parameters.AddWithValue("$key", fileKey);

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public bool ContainsSopInstanceUid(string sopInstanceUid)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM queue_items WHERE sop_instance_uid=$uid UNION ALL SELECT 1 FROM sent_sop_instances WHERE sop_instance_uid=$uid);";
        command.Parameters.AddWithValue("$uid", sopInstanceUid);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public void Enqueue(
        string fileKey,
        string filePath,
        string sopInstanceUid,
        string? patientName = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO queue_items
            (file_key, file_path, status, attempt_count, discovered_at_utc,
             sop_instance_uid, patient_name)
            VALUES ($key, $path, 'Pending', 0, $now, $uid, $patientName);
            """;

        command.Parameters.AddWithValue("$key", fileKey);
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$uid", sopInstanceUid);
        command.Parameters.AddWithValue("$patientName", (object?)patientName ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Enqueue(
        string fileKey,
        string filePath,
        string sopInstanceUid,
        string? patientName,
        string folderId,
        IReadOnlyCollection<PacsSettings> destinations,
        DicomMetadata? metadata = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO queue_items
            (file_key, file_path, status, attempt_count, discovered_at_utc,
             sop_instance_uid, patient_name, folder_id, patient_id, patient_birth_date,
             modality, study_instance_uid, accession_number, study_date)
            VALUES ($key, $path, 'Pending', 0, $now, $uid, $patientName, $folderId,
                    $patientId,$birthDate,$modality,$studyUid,$accession,$studyDate);
            SELECT id FROM queue_items WHERE file_key=$key;
            """;
        command.Parameters.AddWithValue("$key", fileKey);
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$uid", sopInstanceUid);
        command.Parameters.AddWithValue("$patientName", (object?)patientName ?? DBNull.Value);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$patientId", (object?)metadata?.PatientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$birthDate", (object?)metadata?.PatientBirthDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$modality", (object?)metadata?.Modality ?? DBNull.Value);
        command.Parameters.AddWithValue("$studyUid", (object?)metadata?.StudyInstanceUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$accession", (object?)metadata?.AccessionNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$studyDate", (object?)metadata?.StudyDate ?? DBNull.Value);
        var queueItemId = Convert.ToInt64(command.ExecuteScalar());

        foreach (var pacs in destinations)
        {
            using var delivery = connection.CreateCommand();
            delivery.Transaction = transaction;
            delivery.CommandText = """
                INSERT OR IGNORE INTO queue_deliveries
                (queue_item_id, pacs_id, pacs_name, status, attempt_count)
                VALUES ($itemId, $pacsId, $pacsName, 'Pending', 0);
                """;
            delivery.Parameters.AddWithValue("$itemId", queueItemId);
            delivery.Parameters.AddWithValue("$pacsId", pacs.Id);
            delivery.Parameters.AddWithValue("$pacsName", pacs.Name);
            delivery.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public DeliveryItem? GetNextReadyDelivery()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.queue_item_id, d.pacs_id, d.pacs_name,
                   q.file_path, q.sop_instance_uid, d.attempt_count, q.folder_id
            FROM queue_deliveries d
            JOIN queue_items q ON q.id=d.queue_item_id
            WHERE d.status IN ('Pending','Failed')
              AND q.is_archived=0
              AND (d.next_attempt_at_utc IS NULL OR d.next_attempt_at_utc <= $now)
            ORDER BY d.id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new DeliveryItem
        {
            Id = reader.GetInt64(0),
            QueueItemId = reader.GetInt64(1),
            PacsId = reader.GetString(2),
            PacsName = reader.GetString(3),
            FilePath = reader.GetString(4),
            SopInstanceUid = reader.IsDBNull(5) ? null : reader.GetString(5),
            AttemptCount = reader.GetInt32(6),
            FolderId = reader.IsDBNull(7) ? null : reader.GetString(7)
        };
    }

    public void EnsureDestinationsForExisting(
        string fileKey,
        IReadOnlyCollection<PacsSettings> destinations)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status FROM queue_items WHERE file_key=$key;";
        command.Parameters.AddWithValue("$key", fileKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || string.Equals(reader.GetString(1), "Sent", StringComparison.OrdinalIgnoreCase))
            return;
        var itemId = reader.GetInt64(0);
        reader.Close();
        foreach (var pacs in destinations)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO queue_deliveries(queue_item_id,pacs_id,pacs_name,status,attempt_count) VALUES($item,$pacs,$name,'Pending',0);";
            insert.Parameters.AddWithValue("$item", itemId);
            insert.Parameters.AddWithValue("$pacs", pacs.Id);
            insert.Parameters.AddWithValue("$name", pacs.Name);
            insert.ExecuteNonQuery();
        }
    }

    public void MarkDeliverySending(DeliveryItem delivery)
    {
        Execute("""
            UPDATE queue_deliveries SET status='Sending', attempt_count=attempt_count+1,
                last_attempt_at_utc=$now, last_error=NULL WHERE id=$id;
            UPDATE queue_items SET status='Sending', last_attempt_at_utc=$now WHERE id=$itemId;
            """, command =>
        {
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", delivery.Id);
            command.Parameters.AddWithValue("$itemId", delivery.QueueItemId);
        });
    }

    public void MarkDeliverySent(DeliveryItem delivery, string pacsStatus)
    {
        Execute("""
            UPDATE queue_deliveries SET status='Sent', sent_at_utc=$now, pacs_status=$pacsStatus,
                next_attempt_at_utc=NULL, last_error=NULL WHERE id=$id;
            INSERT OR IGNORE INTO sent_sop_instances(sop_instance_uid,first_sent_at_utc)
            SELECT sop_instance_uid,$now FROM queue_items WHERE id=$itemId AND sop_instance_uid IS NOT NULL;
            """, command =>
        {
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$pacsStatus", pacsStatus);
            command.Parameters.AddWithValue("$id", delivery.Id);
            command.Parameters.AddWithValue("$itemId", delivery.QueueItemId);
        });
        RecalculateQueueItem(delivery.QueueItemId);
    }

    public void MarkDeliveryFailed(DeliveryItem delivery, string error, DateTime? nextAttempt, bool exhausted)
    {
        Execute("""
            UPDATE queue_deliveries SET status=$status, last_error=$error,
                next_attempt_at_utc=$next WHERE id=$id;
            """, command =>
        {
            command.Parameters.AddWithValue("$status", exhausted ? "Exhausted" : "Failed");
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$next", nextAttempt is null ? DBNull.Value : nextAttempt.Value.ToString("O"));
            command.Parameters.AddWithValue("$id", delivery.Id);
        });
        RecalculateQueueItem(delivery.QueueItemId);
    }

    public void MarkDeliveryUnavailable(DeliveryItem delivery, string error, DateTime nextAttempt)
    {
        Execute("""
            UPDATE queue_deliveries SET status='Failed', attempt_count=MAX(0,attempt_count-1),
                last_error=$error, next_attempt_at_utc=$next WHERE id=$id;
            """, command =>
        {
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$next", nextAttempt.ToString("O"));
            command.Parameters.AddWithValue("$id", delivery.Id);
        });
        RecalculateQueueItem(delivery.QueueItemId);
    }

    public void MarkDeliveryPendingAfterCancellation(DeliveryItem delivery)
    {
        Execute("UPDATE queue_deliveries SET status='Pending', next_attempt_at_utc=NULL, last_error=NULL WHERE id=$id;",
            command => command.Parameters.AddWithValue("$id", delivery.Id));
        RecalculateQueueItem(delivery.QueueItemId);
    }

    public bool AreAllDeliveriesSent(long queueItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*)=0 FROM queue_deliveries WHERE queue_item_id=$id AND status<>'Sent';";
        command.Parameters.AddWithValue("$id", queueItemId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private void RecalculateQueueItem(long queueItemId)
    {
        Execute("""
            UPDATE queue_items SET
              status = CASE
                WHEN NOT EXISTS(SELECT 1 FROM queue_deliveries WHERE queue_item_id=$id AND status<>'Sent') THEN 'Sent'
                WHEN EXISTS(SELECT 1 FROM queue_deliveries WHERE queue_item_id=$id AND status='Sent') THEN 'Partial'
                WHEN EXISTS(SELECT 1 FROM queue_deliveries WHERE queue_item_id=$id AND status='Exhausted') THEN 'Exhausted'
                WHEN EXISTS(SELECT 1 FROM queue_deliveries WHERE queue_item_id=$id AND status='Failed') THEN 'Failed'
                ELSE 'Pending' END,
              sent_at_utc = CASE WHEN NOT EXISTS(SELECT 1 FROM queue_deliveries WHERE queue_item_id=$id AND status<>'Sent') THEN $now ELSE sent_at_utc END,
              attempt_count = COALESCE((SELECT MAX(attempt_count) FROM queue_deliveries WHERE queue_item_id=$id),0),
              last_error = (SELECT group_concat(pacs_name || ': ' || last_error, '; ') FROM queue_deliveries WHERE queue_item_id=$id AND last_error IS NOT NULL)
            WHERE id=$id;
            """, command =>
        {
            command.Parameters.AddWithValue("$id", queueItemId);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        });
    }

    public QueueItem? GetNextReady()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, file_key, file_path, status, attempt_count,
                   discovered_at_utc, last_attempt_at_utc, sent_at_utc,
                   sop_instance_uid, pacs_status, last_error, patient_name,
                   (SELECT group_concat(pacs_name || ': ' || CASE status WHEN 'Pending' THEN 'ожидает' WHEN 'Sending' THEN 'отправляется' WHEN 'Sent' THEN 'отправлено' WHEN 'Failed' THEN 'ошибка' WHEN 'Exhausted' THEN 'лимит попыток' ELSE status END, '; ') FROM queue_deliveries WHERE queue_item_id=queue_items.id),
                   folder_id,patient_id,patient_birth_date,modality,study_instance_uid,accession_number,study_date
            FROM queue_items
            WHERE status IN ('Pending', 'Failed')
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)
            ORDER BY id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void MarkSending(long id)
    {
        Execute(
            """
            UPDATE queue_items
            SET status='Sending',
                attempt_count=attempt_count+1,
                last_attempt_at_utc=$now,
                last_error=NULL
            WHERE id=$id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$id", id);
            });
    }

    public void MarkPendingAfterCancellation(long id)
    {
        Execute(
            "UPDATE queue_items SET status='Pending', next_attempt_at_utc=NULL, last_error=NULL WHERE id=$id;",
            command => command.Parameters.AddWithValue("$id", id));
    }

    public void MarkSent(long id, string? uid, string status)
    {
        Execute(
            """
            UPDATE queue_items
            SET status='Sent',
                sent_at_utc=$now,
                sop_instance_uid=$uid,
                pacs_status=$status,
                next_attempt_at_utc=NULL,
                last_error=NULL
            WHERE id=$id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$uid", (object?)uid ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$id", id);
            });
    }

    public void MarkFailed(long id, string error, DateTime? nextAttempt, bool attemptsExhausted = false)
    {
        Execute(
            """
            UPDATE queue_items
            SET status=$status,
                last_error=$error,
                next_attempt_at_utc=$next
            WHERE id=$id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$status", attemptsExhausted ? "Exhausted" : "Failed");
                command.Parameters.AddWithValue("$error", error);
                command.Parameters.AddWithValue("$next", nextAttempt is null ? DBNull.Value : nextAttempt.Value.ToString("O"));
                command.Parameters.AddWithValue("$id", id);
            });
    }
public int RetryFailedAndStuck()
{
    using var connection = Open();
    using var command = connection.CreateCommand();

    command.CommandText = """
        UPDATE queue_items
        SET status = 'Pending',
            next_attempt_at_utc = NULL,
            last_error = NULL,
            attempt_count = 0
        WHERE status IN ('Failed', 'Sending', 'Exhausted');
        UPDATE queue_deliveries
        SET status='Pending', next_attempt_at_utc=NULL, last_error=NULL, attempt_count=0
        WHERE status IN ('Failed','Sending','Exhausted');
        """;

    return command.ExecuteNonQuery();
}
    public int ArchiveSentHistory()
{
    using var connection = Open();
    using var command = connection.CreateCommand();

    command.CommandText = """
        UPDATE queue_items
        SET is_archived = 1
        WHERE status = 'Sent'
          AND is_archived = 0;
        """;

    return command.ExecuteNonQuery();
}
    public int ArchiveAllHistory()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE queue_items SET is_archived=1 WHERE is_archived=0;";
        return command.ExecuteNonQuery();
    }

    public void ArchiveItem(long id) => Execute(
        "UPDATE queue_items SET is_archived=1 WHERE id=$id;",
        command => command.Parameters.AddWithValue("$id", id));

    public void ApplyStudyDateFilters(IEnumerable<WatchFolderSettings> folders)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var restore = connection.CreateCommand())
        {
            restore.Transaction = transaction;
            restore.CommandText = "UPDATE queue_items SET is_archived=0,is_date_filtered=0 WHERE is_date_filtered=1 AND status<>'Sent';";
            restore.ExecuteNonQuery();
        }
        foreach (var folder in folders.Where(f => f.Enabled && f.SendStudiesFromDate.HasValue))
        {
            using var filter = connection.CreateCommand();
            filter.Transaction = transaction;
            filter.CommandText = """
                UPDATE queue_items SET is_archived=1,is_date_filtered=1
                WHERE folder_id=$folder AND status<>'Sent'
                  AND (study_date IS NULL OR study_date='' OR study_date < $date);
                """;
            filter.Parameters.AddWithValue("$folder", folder.Id);
            filter.Parameters.AddWithValue("$date", folder.SendStudiesFromDate!.Value.ToString("yyyyMMdd"));
            filter.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public QueueItem? GetItem(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, file_key, file_path, status, attempt_count,
                   discovered_at_utc, last_attempt_at_utc, sent_at_utc,
                   sop_instance_uid, pacs_status, last_error, patient_name,
                   (SELECT group_concat(pacs_name || ': ' || status, '; ') FROM queue_deliveries WHERE queue_item_id=queue_items.id),
                   folder_id,patient_id,patient_birth_date,modality,study_instance_uid,accession_number,study_date
            FROM queue_items WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<DeliveryDetails> GetDeliveryDetails(long queueItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,pacs_name,status,attempt_count,last_attempt_at_utc,sent_at_utc,pacs_status,last_error
            FROM queue_deliveries WHERE queue_item_id=$id ORDER BY id;
            """;
        command.Parameters.AddWithValue("$id", queueItemId);
        using var reader = command.ExecuteReader();
        var result = new List<DeliveryDetails>();
        while (reader.Read()) result.Add(new DeliveryDetails
        {
            Id = reader.GetInt64(0),
            PacsName = reader.GetString(1),
            Status = TranslateDeliveryStatus(reader.GetString(2)),
            AttemptCount = reader.GetInt32(3),
            LastAttemptAtUtc = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)).ToLocalTime(),
            SentAtUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)).ToLocalTime(),
            PacsStatus = reader.IsDBNull(6) ? null : reader.GetString(6),
            LastError = reader.IsDBNull(7) ? (reader.IsDBNull(6) ? null : reader.GetString(6)) : reader.GetString(7)
        });
        return result;
    }

    public int RetryQueueItem(long queueItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_deliveries SET status='Pending',attempt_count=0,next_attempt_at_utc=NULL,last_error=NULL
            WHERE queue_item_id=$id AND status<>'Sent';
            UPDATE queue_items SET status='Pending',attempt_count=0,last_error=NULL,is_archived=0
            WHERE id=$id AND EXISTS(SELECT 1 FROM queue_deliveries WHERE queue_item_id=$id AND status<>'Sent');
            """;
        command.Parameters.AddWithValue("$id", queueItemId);
        return command.ExecuteNonQuery();
    }

    public int RetryDelivery(long deliveryId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_deliveries SET status='Pending',attempt_count=0,next_attempt_at_utc=NULL,last_error=NULL WHERE id=$id AND status<>'Sent';
            UPDATE queue_items SET status='Pending',last_error=NULL,is_archived=0
            WHERE id=(SELECT queue_item_id FROM queue_deliveries WHERE id=$id AND status<>'Sent');
            """;
        command.Parameters.AddWithValue("$id", deliveryId);
        return command.ExecuteNonQuery();
    }

    private static string TranslateDeliveryStatus(string status) => status switch
    {
        "Pending" => "Ожидает",
        "Sending" => "Отправляется",
        "Sent" => "Отправлено",
        "Failed" => "Ошибка",
        "Exhausted" => "Лимит попыток",
        _ => status
    };
    public QueueCounts GetCounts(DateTime? sentCutoffUtc = null)
{
    using var connection = Open();
    using var command = connection.CreateCommand();

    command.CommandText = """
        SELECT
          SUM(CASE WHEN status='Pending' THEN 1 ELSE 0 END),
          SUM(CASE WHEN status='Sending' THEN 1 ELSE 0 END),
          SUM(CASE WHEN status='Sent' THEN 1 ELSE 0 END),
          SUM(CASE WHEN status IN ('Failed', 'Exhausted', 'Partial') THEN 1 ELSE 0 END)
        FROM queue_items
        WHERE is_archived = 0
          AND (status <> 'Sent' OR $cutoff IS NULL OR sent_at_utc >= $cutoff);
        """;

    command.Parameters.AddWithValue(
        "$cutoff",
        sentCutoffUtc is null ? DBNull.Value : sentCutoffUtc.Value.ToString("O"));

    using var reader = command.ExecuteReader();
    reader.Read();

    return new QueueCounts(
        reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
        reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
        reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
        reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
}

   public IReadOnlyList<QueueItem> GetRecent(int limit, DateTime? sentCutoffUtc = null)
{
    using var connection = Open();
    using var command = connection.CreateCommand();

    command.CommandText = """
        SELECT id, file_key, file_path, status, attempt_count,
               discovered_at_utc, last_attempt_at_utc, sent_at_utc,
               sop_instance_uid, pacs_status, last_error, patient_name,
               (SELECT group_concat(pacs_name || ': ' || CASE status WHEN 'Pending' THEN 'ожидает' WHEN 'Sending' THEN 'отправляется' WHEN 'Sent' THEN 'отправлено' WHEN 'Failed' THEN 'ошибка' WHEN 'Exhausted' THEN 'лимит попыток' ELSE status END, '; ') FROM queue_deliveries WHERE queue_item_id=queue_items.id),
               folder_id,patient_id,patient_birth_date,modality,study_instance_uid,accession_number,study_date
        FROM queue_items
        WHERE is_archived = 0
          AND (status <> 'Sent' OR $cutoff IS NULL OR sent_at_utc >= $cutoff)
        ORDER BY id DESC
        LIMIT $limit;
        """;

    command.Parameters.AddWithValue("$limit", limit);
    command.Parameters.AddWithValue(
        "$cutoff",
        sentCutoffUtc is null ? DBNull.Value : sentCutoffUtc.Value.ToString("O"));

    using var reader = command.ExecuteReader();
    var result = new List<QueueItem>();

    while (reader.Read())
        result.Add(Read(reader));

    return result;
}

    private void Initialize()
{
    using var connection = Open();
    using var command = connection.CreateCommand();

    command.CommandText = """
        PRAGMA journal_mode=WAL;
        PRAGMA busy_timeout=5000;

        CREATE TABLE IF NOT EXISTS queue_items (
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
            last_error TEXT NULL,
            patient_name TEXT NULL,
            folder_id TEXT NULL,
            patient_id TEXT NULL,
            patient_birth_date TEXT NULL,
            modality TEXT NULL,
            study_instance_uid TEXT NULL,
            accession_number TEXT NULL,
            study_date TEXT NULL,
            is_archived INTEGER NOT NULL DEFAULT 0,
            is_date_filtered INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS queue_deliveries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            queue_item_id INTEGER NOT NULL,
            pacs_id TEXT NOT NULL,
            pacs_name TEXT NOT NULL,
            status TEXT NOT NULL,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            last_attempt_at_utc TEXT NULL,
            next_attempt_at_utc TEXT NULL,
            sent_at_utc TEXT NULL,
            pacs_status TEXT NULL,
            last_error TEXT NULL,
            UNIQUE(queue_item_id, pacs_id),
            FOREIGN KEY(queue_item_id) REFERENCES queue_items(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS sent_sop_instances (
            sop_instance_uid TEXT PRIMARY KEY,
            first_sent_at_utc TEXT NOT NULL
        );

        INSERT OR IGNORE INTO sent_sop_instances(sop_instance_uid,first_sent_at_utc)
        SELECT sop_instance_uid,COALESCE(sent_at_utc,discovered_at_utc)
        FROM queue_items WHERE status='Sent' AND sop_instance_uid IS NOT NULL;

        UPDATE queue_deliveries SET status='Failed', next_attempt_at_utc=$now,
            last_error=COALESCE(last_error, 'Предыдущий запуск завершился во время отправки.')
        WHERE status='Sending';

        UPDATE queue_items
        SET status='Failed',
            next_attempt_at_utc=$now,
            last_error=COALESCE(
                last_error,
                'Предыдущий запуск завершился во время отправки.'
            )
        WHERE status='Sending';
        """;

    command.Parameters.AddWithValue(
        "$now",
        DateTime.UtcNow.ToString("O"));

    command.ExecuteNonQuery();
    EnsureColumn("is_archived", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn("is_date_filtered", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn("patient_name", "TEXT NULL");
    EnsureColumn("folder_id", "TEXT NULL");
    EnsureColumn("patient_id", "TEXT NULL");
    EnsureColumn("patient_birth_date", "TEXT NULL");
    EnsureColumn("modality", "TEXT NULL");
    EnsureColumn("study_instance_uid", "TEXT NULL");
    EnsureColumn("accession_number", "TEXT NULL");
    EnsureColumn("study_date", "TEXT NULL");

}
private void EnsureColumn(string columnName, string definition)
{
    using var connection = Open();

    using var checkCommand = connection.CreateCommand();
    checkCommand.CommandText =
        "PRAGMA table_info(queue_items);";

    using var reader = checkCommand.ExecuteReader();

    var columnExists = false;

    while (reader.Read())
    {
        if (string.Equals(
                reader.GetString(1),
                columnName,
                StringComparison.OrdinalIgnoreCase))
        {
            columnExists = true;
            break;
        }
    }

    reader.Close();

    if (columnExists)
        return;

    using var alterCommand = connection.CreateCommand();
    alterCommand.CommandText =
        $"ALTER TABLE queue_items ADD COLUMN {columnName} {definition};";

    alterCommand.ExecuteNonQuery();
}
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void CreateMigrationBackupIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='queue_items') AND NOT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='queue_deliveries');";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) return;
        connection.Close();
        File.Copy(path, $"{path}.pre-multipacs-{DateTime.Now:yyyyMMddHHmmss}.bak", overwrite: false);
    }

    private void Execute(string sql, Action<SqliteCommand> configure)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = sql;
        configure(command);
        command.ExecuteNonQuery();
    }

    public void BackupTo(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var source = Open();
        using var destination = new SqliteConnection($"Data Source={fullPath};Mode=ReadWriteCreate");
        destination.Open();
        source.BackupDatabase(destination);
    }

    public int CleanupOldRecords(int retentionDays)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM queue_items
            WHERE (status='Sent' OR is_archived=1)
              AND COALESCE(sent_at_utc,last_attempt_at_utc,discovered_at_utc) < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("O"));
        return command.ExecuteNonQuery();
    }

    public string CheckIntegrityAndOptimize()
    {
        using var connection = Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(check.ExecuteScalar()) ?? "unknown";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite integrity_check: {result}");
        using var optimize = connection.CreateCommand();
        optimize.CommandText = "PRAGMA optimize;";
        optimize.ExecuteNonQuery();
        return result;
    }

    public DailySummary GetDailySummary(DateTime localDay)
    {
        var start = localDay.Date.ToUniversalTime();
        var end = localDay.Date.AddDays(1).ToUniversalTime();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN status='Sent' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN status IN ('Failed','PartiallySent') THEN 1 ELSE 0 END)
            FROM queue_items WHERE discovered_at_utc >= $start AND discovered_at_utc < $end;
            """;
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new DailySummary(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
    }

    private static QueueItem Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        FileKey = reader.GetString(1),
        FilePath = reader.GetString(2),
        Status = Enum.Parse<QueueStatus>(reader.GetString(3), true),
        AttemptCount = reader.GetInt32(4),
        DiscoveredAtUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
        LastAttemptAtUtc = reader.IsDBNull(6)
            ? null
            : DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
        SentAtUtc = reader.IsDBNull(7)
            ? null
            : DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
        SopInstanceUid = reader.IsDBNull(8) ? null : reader.GetString(8),
        PacsStatus = reader.IsDBNull(9) ? null : reader.GetString(9),
        LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
        PatientName = reader.IsDBNull(11) ? null : reader.GetString(11),
        DeliverySummary = reader.IsDBNull(12) ? null : reader.GetString(12),
        FolderId = reader.IsDBNull(13) ? null : reader.GetString(13),
        PatientId = reader.IsDBNull(14) ? null : reader.GetString(14),
        PatientBirthDate = reader.IsDBNull(15) ? null : reader.GetString(15),
        Modality = reader.IsDBNull(16) ? null : reader.GetString(16),
        StudyInstanceUid = reader.IsDBNull(17) ? null : reader.GetString(17),
        AccessionNumber = reader.IsDBNull(18) ? null : reader.GetString(18),
        StudyDate = reader.IsDBNull(19) ? null : reader.GetString(19)
    };
}

public sealed record DailySummary(int Found, int Sent, int WithErrors);
