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

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
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

    public void Enqueue(string fileKey, string filePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO queue_items
            (file_key, file_path, status, attempt_count, discovered_at_utc)
            VALUES ($key, $path, 'Pending', 0, $now);
            """;

        command.Parameters.AddWithValue("$key", fileKey);
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public QueueItem? GetNextReady()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, file_key, file_path, status, attempt_count,
                   discovered_at_utc, last_attempt_at_utc, sent_at_utc,
                   sop_instance_uid, pacs_status, last_error
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

    public void MarkFailed(long id, string error, DateTime nextAttempt)
    {
        Execute(
            """
            UPDATE queue_items
            SET status='Failed',
                last_error=$error,
                next_attempt_at_utc=$next
            WHERE id=$id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$error", error);
                command.Parameters.AddWithValue("$next", nextAttempt.ToString("O"));
                command.Parameters.AddWithValue("$id", id);
            });
    }

    public QueueCounts GetCounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
              SUM(CASE WHEN status='Pending' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status='Sending' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status='Sent' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status='Failed' THEN 1 ELSE 0 END)
            FROM queue_items;
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        return new QueueCounts(
            reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
    }

    public IReadOnlyList<QueueItem> GetRecent(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, file_key, file_path, status, attempt_count,
                   discovered_at_utc, last_attempt_at_utc, sent_at_utc,
                   sop_instance_uid, pacs_status, last_error
            FROM queue_items
            ORDER BY id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

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
            last_error TEXT NULL
        );

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
}

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql, Action<SqliteCommand> configure)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = sql;
        configure(command);
        command.ExecuteNonQuery();
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
        LastError = reader.IsDBNull(10) ? null : reader.GetString(10)
    };
}
