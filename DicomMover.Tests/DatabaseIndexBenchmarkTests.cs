using System.Diagnostics;
using DicomMover.Models;
using DicomMover.Services;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DicomMover.Tests;

public sealed class DatabaseIndexBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DbIndexTests-" + Guid.NewGuid().ToString("N"));

    public DatabaseIndexBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void ExplainQueryPlan_VerifiesIndexesAreUsed_ForCriticalQueries()
    {
        var dbPath = Path.Combine(_dir, "plan_test.db");
        var db = new Database(dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // 1. Next ready delivery
        var plan1 = GetExplainPlan(conn, """
            EXPLAIN QUERY PLAN
            SELECT d.id, d.queue_item_id, d.pacs_id, d.pacs_name,
                   q.file_path, q.sop_instance_uid, d.attempt_count, q.folder_id
            FROM queue_deliveries d
            JOIN queue_items q ON q.id=d.queue_item_id
            WHERE d.status IN ('Pending','Failed')
              AND q.is_archived=0
              AND (d.next_attempt_at_utc IS NULL OR d.next_attempt_at_utc <= '2026-09-02T00:00:00Z')
            ORDER BY CASE d.status WHEN 'Pending' THEN 0 ELSE 1 END,
                     COALESCE(d.next_attempt_at_utc, q.discovered_at_utc),
                     d.id
            LIMIT 1;
            """);
        _output.WriteLine($"Plan 1 (NextReadyDelivery):\n{plan1}");
        Assert.Contains("USING INDEX", plan1);

        // 2. Main screen queue items
        var plan2 = GetExplainPlan(conn, """
            EXPLAIN QUERY PLAN
            SELECT id, file_key, file_path, status, attempt_count,
                   discovered_at_utc, last_attempt_at_utc, sent_at_utc,
                   sop_instance_uid, pacs_status, last_error, patient_name,
                   (SELECT group_concat(pacs_name || ': ' || status, '; ') FROM queue_deliveries WHERE queue_item_id=queue_items.id),
                   folder_id,patient_id,patient_birth_date,modality,study_instance_uid,accession_number,study_date
            FROM queue_items
            WHERE is_archived = 0
              AND (status <> 'Sent' OR '2026-09-01T00:00:00Z' IS NULL OR sent_at_utc >= '2026-09-01T00:00:00Z')
            ORDER BY id DESC
            LIMIT 50;
            """);
        _output.WriteLine($"Plan 2 (GetRecentQueueItems):\n{plan2}");
        Assert.Contains("queue_deliveries", plan2);

        // 3. SopInstanceUid existence
        var plan3 = GetExplainPlan(conn, """
            EXPLAIN QUERY PLAN
            SELECT EXISTS(SELECT 1 FROM queue_items WHERE sop_instance_uid='1.2.840.10008.1' UNION ALL SELECT 1 FROM sent_sop_instances WHERE sop_instance_uid='1.2.840.10008.1');
            """);
        _output.WriteLine($"Plan 3 (ContainsSopInstanceUid):\n{plan3}");
        Assert.Contains("idx_queue_items_sop_uid", plan3);

        // 4. Cleanup old records
        var plan4 = GetExplainPlan(conn, """
            EXPLAIN QUERY PLAN
            DELETE FROM queue_items
            WHERE (status='Sent' OR is_archived=1)
              AND COALESCE(sent_at_utc,last_attempt_at_utc,discovered_at_utc) < '2026-08-01T00:00:00Z';
            """);
        _output.WriteLine($"Plan 4 (CleanupOldRecords):\n{plan4}");
        Assert.NotNull(plan4);
    }

    [Fact]
    public void ExplainQueryPlan_BeforeAndAfterIndexes_Comparison()
    {
        var rawDbPath = Path.Combine(_dir, "no_index.db");
        using (var rawConn = new SqliteConnection($"Data Source={rawDbPath}"))
        {
            rawConn.Open();
            using var cmd = rawConn.CreateCommand();
            cmd.CommandText = """
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
                CREATE TABLE queue_deliveries (
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
                    last_error TEXT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            // BEFORE indexes
            var planSopBefore = GetExplainPlan(rawConn, "EXPLAIN QUERY PLAN SELECT 1 FROM queue_items WHERE sop_instance_uid='1.2.3';");
            var planReadyBefore = GetExplainPlan(rawConn, "EXPLAIN QUERY PLAN SELECT 1 FROM queue_deliveries WHERE status IN ('Pending','Failed');");
            var planHistBefore = GetExplainPlan(rawConn, "EXPLAIN QUERY PLAN SELECT 1 FROM queue_items WHERE is_archived=0 AND (status<>'Sent' OR sent_at_utc>='2026-09-01');");

            _output.WriteLine("=== EXPLAIN QUERY PLAN ДО ИНДЕКСОВ ===");
            _output.WriteLine($"SOP UID Lookup: {planSopBefore}");
            _output.WriteLine($"Ready Deliveries: {planReadyBefore}");
            _output.WriteLine($"History/Queue: {planHistBefore}");
            Assert.Contains("SCAN", planSopBefore);
            Assert.Contains("SCAN", planReadyBefore);

            // CREATE INDEXES
            cmd.CommandText = """
                CREATE INDEX idx_queue_items_sop_uid ON queue_items(sop_instance_uid);
                CREATE INDEX idx_queue_deliveries_ready ON queue_deliveries(status, next_attempt_at_utc);
                CREATE INDEX idx_queue_items_history ON queue_items(is_archived, status, sent_at_utc);
                """;
            cmd.ExecuteNonQuery();

            // AFTER indexes
            var planSopAfter = GetExplainPlan(rawConn, "EXPLAIN QUERY PLAN SELECT 1 FROM queue_items WHERE sop_instance_uid='1.2.3';");
            var planReadyAfter = GetExplainPlan(rawConn, "EXPLAIN QUERY PLAN SELECT 1 FROM queue_deliveries WHERE status IN ('Pending','Failed');");
            var planHistAfter = GetExplainPlan(rawConn, "EXPLAIN QUERY PLAN SELECT 1 FROM queue_items WHERE is_archived=0 AND (status<>'Sent' OR sent_at_utc>='2026-09-01');");

            _output.WriteLine("\n=== EXPLAIN QUERY PLAN ПОСЛЕ ИНДЕКСОВ ===");
            _output.WriteLine($"SOP UID Lookup: {planSopAfter}");
            _output.WriteLine($"Ready Deliveries: {planReadyAfter}");
            _output.WriteLine($"History/Queue: {planHistAfter}");
            Assert.Contains("idx_queue_items_sop_uid", planSopAfter);
            Assert.Contains("idx_queue_deliveries_ready", planReadyAfter);
            Assert.Contains("idx_queue_items_history", planHistAfter);
        }
    }

    [Fact]
    public void Compare_MemoryStream_Vs_TempFile_Transcode_200MB()
    {
        // 200 MB synthetic Pixel Data
        const int pixelBytesCount = 200 * 1024 * 1024;
        var path = Path.Combine(_dir, "synth_200mb.dcm");

        CreateLargeDicomFile(path, pixelBytesCount);

        var rule = new EncodingRule
        {
            Name = "Transcode 200MB",
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        };

        // --- ТЕКУЩИЙ ПРОИЗВОДСТВЕННЫЙ ВАРИАНТ (Временный файл + SkipLargeTags) ---
        ForceGarbageCollection();
        var memBeforeTemp = GC.GetTotalMemory(true);

        var transcoder = new DicomTextTranscoder();
        var result = transcoder.BuildTransformedDicom(path, rule);
        var memDuringTemp = GC.GetTotalMemory(false);
        var deltaTempMb = (memDuringTemp - memBeforeTemp) / (1024.0 * 1024.0);

        if (File.Exists(result.TempFilePath))
            File.Delete(result.TempFilePath);

        result = null;
        transcoder = null;
        ForceGarbageCollection();
        var memAfterTemp = GC.GetTotalMemory(true);

        // --- ПРЕЖНИЙ ВАРИАНТ (Проверочная сериализация в MemoryStream) ---
        ForceGarbageCollection();
        var memBeforeMs = GC.GetTotalMemory(true);

        var file1 = FellowOakDicom.DicomFile.Open(path);
        using var ms = new MemoryStream();
        file1.Save(ms);
        ms.Position = 0;
        var verifiedMs = FellowOakDicom.DicomFile.Open(ms);
        var memDuringMs = GC.GetTotalMemory(false);
        var deltaMsMb = (memDuringMs - memBeforeMs) / (1024.0 * 1024.0);

        ms.Dispose();
        file1 = null;
        verifiedMs = null;
        ForceGarbageCollection();
        var memAfterMs = GC.GetTotalMemory(true);

        _output.WriteLine("=== СРАВНИТЕЛЬНЫЙ АНАЛИЗ ПАМЯТИ (Pixel Data = 200 МБ) ===");
        _output.WriteLine("1. Текущий производственный вариант (потоковая запись + SkipLargeTags):");
        _output.WriteLine($"   Память GC до транскодирования: {memBeforeTemp / (1024.0 * 1024.0):F2} МБ");
        _output.WriteLine($"   Пиковая память во время верификации: {memDuringTemp / (1024.0 * 1024.0):F2} МБ");
        _output.WriteLine($"   Пиковая дельта памяти (без загрузки PixelData): {deltaTempMb:F2} МБ");
        _output.WriteLine($"   Память после освобождения и LOH-компактификации: {memAfterTemp / (1024.0 * 1024.0):F2} МБ");

        _output.WriteLine("\n2. Прежний вариант (проверочная сериализация в MemoryStream):");
        _output.WriteLine($"   Память GC до транскодирования: {memBeforeMs / (1024.0 * 1024.0):F2} МБ");
        _output.WriteLine($"   Пиковая память во время верификации: {memDuringMs / (1024.0 * 1024.0):F2} МБ");
        _output.WriteLine($"   Пиковая дельта памяти (дублирование в RAM): {deltaMsMb:F2} МБ");
        _output.WriteLine($"   Память после освобождения и LOH-компактификации: {memAfterMs / (1024.0 * 1024.0):F2} МБ");

        Assert.True(deltaTempMb < 10.0, $"Current production transcode delta ({deltaTempMb:F2} MB) must not load 200 MB PixelData into RAM.");
        Assert.True(deltaTempMb < deltaMsMb, $"Current production ({deltaTempMb:F2} MB) must allocate significantly less RAM than MemoryStream ({deltaMsMb:F2} MB).");

        File.Delete(path);
    }

    private static void CreateLargeDicomFile(string path, int sizeBytes)
    {
        var buffer = new byte[sizeBytes];
        new Random(42).NextBytes(buffer);
        var dataset = new FellowOakDicom.DicomDataset(FellowOakDicom.DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { FellowOakDicom.DicomTag.SpecificCharacterSet, "ISO_IR 100" },
            { FellowOakDicom.DicomTag.SOPClassUID, FellowOakDicom.DicomUID.SecondaryCaptureImageStorage },
            { FellowOakDicom.DicomTag.SOPInstanceUID, FellowOakDicom.DicomUIDGenerator.GenerateDerivedFromUUID() },
            { FellowOakDicom.DicomTag.StudyInstanceUID, FellowOakDicom.DicomUIDGenerator.GenerateDerivedFromUUID() },
            { FellowOakDicom.DicomTag.SeriesInstanceUID, FellowOakDicom.DicomUIDGenerator.GenerateDerivedFromUUID() },
            { FellowOakDicom.DicomTag.PatientName, "Крупный^200МБ" },
            new FellowOakDicom.DicomOtherByte(FellowOakDicom.DicomTag.PixelData, new FellowOakDicom.IO.Buffer.MemoryByteBuffer(buffer))
        };
        new FellowOakDicom.DicomFile(dataset).Save(path);
    }

    private static void ForceGarbageCollection()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(50000)]
    public void Benchmark_SyntheticDatabaseQueries(int recordCount)
    {
        var dbPath = Path.Combine(_dir, $"bench_{recordCount}.db");
        var db = new Database(dbPath);

        PopulateSyntheticData(dbPath, recordCount);

        // Benchmark 1: ContainsSopInstanceUid
        var sw = Stopwatch.StartNew();
        var exists = db.ContainsSopInstanceUid("1.2.840.10008.synthetic." + (recordCount / 2));
        sw.Stop();
        var sopCheckMs = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"Records: {recordCount}, ContainsSopInstanceUid: {sopCheckMs:0.##} ms, exists: {exists}");
        Assert.True(exists);
        Assert.True(sopCheckMs < 50, $"ContainsSopInstanceUid took too long: {sopCheckMs} ms");

        // Benchmark 2: GetNextReadyDelivery
        sw.Restart();
        var nextDelivery = db.GetNextReadyDelivery();
        sw.Stop();
        var nextDeliveryMs = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"Records: {recordCount}, GetNextReadyDelivery: {nextDeliveryMs:0.##} ms");
        Assert.NotNull(nextDelivery);
        Assert.True(nextDeliveryMs < 50, $"GetNextReadyDelivery took too long: {nextDeliveryMs} ms");

        // Benchmark 3: GetRecentQueueItems (Main screen)
        sw.Restart();
        var recent = db.GetRecent(limit: 50, sentCutoffUtc: DateTime.UtcNow.AddDays(-1));
        sw.Stop();
        var recentMs = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"Records: {recordCount}, GetRecent: {recentMs:0.##} ms, count: {recent.Count}");
        Assert.NotEmpty(recent);
        Assert.True(recentMs < 100, $"GetRecent took too long: {recentMs} ms");

        // Benchmark 4: CleanupOldRecords
        sw.Restart();
        var cleaned = db.CleanupOldRecords(retentionDays: 30);
        sw.Stop();
        var cleanupMs = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"Records: {recordCount}, CleanupOldRecords: {cleanupMs:0.##} ms, cleaned: {cleaned}");
        Assert.True(cleanupMs < 500, $"CleanupOldRecords took too long: {cleanupMs} ms");
    }

    private static void PopulateSyntheticData(string dbPath, int count)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();

        using var cmdItem = conn.CreateCommand();
        cmdItem.Transaction = tx;
        cmdItem.CommandText = """
            INSERT INTO queue_items
            (file_key, file_path, status, attempt_count, discovered_at_utc,
             sent_at_utc, sop_instance_uid, patient_name, is_archived)
            VALUES ($key, $path, $status, 0, $discovered, $sent, $sop, $name, 0);
            SELECT last_insert_rowid();
            """;
        var pKey = cmdItem.Parameters.Add("$key", SqliteType.Text);
        var pPath = cmdItem.Parameters.Add("$path", SqliteType.Text);
        var pStatus = cmdItem.Parameters.Add("$status", SqliteType.Text);
        var pDiscovered = cmdItem.Parameters.Add("$discovered", SqliteType.Text);
        var pSent = cmdItem.Parameters.Add("$sent", SqliteType.Text);
        var pSop = cmdItem.Parameters.Add("$sop", SqliteType.Text);
        var pName = cmdItem.Parameters.Add("$name", SqliteType.Text);

        using var cmdDel = conn.CreateCommand();
        cmdDel.Transaction = tx;
        cmdDel.CommandText = """
            INSERT INTO queue_deliveries
            (queue_item_id, pacs_id, pacs_name, status, attempt_count, sent_at_utc)
            VALUES ($itemId, 'pacs-1', 'Main PACS', $delStatus, 0, $delSent);
            """;
        var pItemId = cmdDel.Parameters.Add("$itemId", SqliteType.Integer);
        var pDelStatus = cmdDel.Parameters.Add("$delStatus", SqliteType.Text);
        var pDelSent = cmdDel.Parameters.Add("$delSent", SqliteType.Text);

        var now = DateTime.UtcNow;
        for (var i = 1; i <= count; i++)
        {
            var isSent = i % 5 != 0; // 80% sent, 20% pending
            var status = isSent ? "Sent" : "Pending";
            var time = now.AddMinutes(-i).ToString("O");
            var sentTime = isSent ? time : (object)DBNull.Value;

            pKey.Value = $"key_{i}";
            pPath.Value = $"C:\\Dicom\\file_{i}.dcm";
            pStatus.Value = status;
            pDiscovered.Value = time;
            pSent.Value = sentTime;
            pSop.Value = $"1.2.840.10008.synthetic.{i}";
            pName.Value = $"Patient {i}";

            var itemId = (long)cmdItem.ExecuteScalar()!;

            pItemId.Value = itemId;
            pDelStatus.Value = status;
            pDelSent.Value = sentTime;
            cmdDel.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static string GetExplainPlan(SqliteConnection conn, string query)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        using var reader = cmd.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            var detail = reader["detail"]?.ToString() ?? "";
            lines.Add(detail);
        }
        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
