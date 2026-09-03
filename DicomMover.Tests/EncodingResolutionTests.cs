using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using System.Text;
using Xunit;

namespace DicomMover.Tests;

public sealed class EncodingResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EncodingResTests-" + Guid.NewGuid().ToString("N"));

    public EncodingResolutionTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void CandidateRules_DoNotMatchGlobally_WithoutExplicitSourceFolder()
    {
        var resolver = new EncodingRuleResolver();

        var rule = new EncodingRule
        {
            Id = "r1",
            Name = "Folder 1 rule",
            SourceFolderId = "folder-1",
            DestinationPacsId = "pacs-1",
            Enabled = true
        };

        var rules = new[] { rule };

        // Should match folder-1
        var match = resolver.Resolve(rules, "folder-1", "pacs-1");
        Assert.NotNull(match);
        Assert.Equal("r1", match.Id);

        // Should NOT match folder-2
        var noMatch = resolver.Resolve(rules, "folder-2", "pacs-1");
        Assert.Null(noMatch);

        // Should NOT match null folder
        var noMatchNull = resolver.Resolve(rules, null, "pacs-1");
        Assert.Null(noMatchNull);
    }

    [Fact]
    public void ForcedEncoding_AppliesToAllTags_WithoutBlockingOnShortStrings()
    {
        var path = Path.Combine(_dir, "short_tag.dcm");
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 144" },
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, "Тестов^Тест" },
            { DicomTag.StudyDescription, "КТ" },
            new DicomOtherByte(DicomTag.PixelData, [1, 2, 3, 4])
        };
        new DicomFile(dataset).Save(path);

        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.ASCII.GetBytes("ISO_IR 144");
        var index = Find(bytes, marker);
        Assert.True(index >= 0);
        var replacement = Encoding.ASCII.GetBytes("ISO_IR 100");
        Array.Copy(replacement, 0, bytes, index, replacement.Length);
        File.WriteAllBytes(path, bytes);

        var transcoder = new DicomTextTranscoder();
        var rule = new EncodingRule
        {
            Name = "Force CP1251",
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        };

        var result = transcoder.BuildTransformedDicom(path, rule);

        Assert.NotNull(result);
        Assert.Equal("ISO_IR 192", result.TargetDescription);

        // Clean temp file
        if (File.Exists(result.TempFilePath))
            File.Delete(result.TempFilePath);
    }

    private static int Find(byte[] source, byte[] pattern)
    {
        for (var i = 0; i <= source.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    [Fact]
    public void OneTimeEncodingOverride_SurvivesDatabaseRestart()
    {
        var dbPath = Path.Combine(_dir, "override_restart.db");
        var db1 = new Database(dbPath);

        var pacs = new PacsSettings { Id = "pacs-1", Name = "PACS 1", Enabled = true };
        var folder = new WatchFolderSettings { Id = "folder-1", Name = "Folder 1", Path = _dir, PacsIds = [pacs.Id], Enabled = true };
        db1.Enqueue("key-test-override", "dummy.dcm", "1.2.3.1", "Test", folder.Id, [pacs]);
        var delivery = db1.GetNextReadyDelivery()!;
        var testDeliveryId = delivery.Id;

        db1.SetDeliveryEncodingOverride(testDeliveryId, SourceEncodingMode.ForceWindows1251, TargetDicomEncoding.IsoIr192);

        // Check in same instance
        var retrieved1 = db1.GetDeliveryEncodingOverride(testDeliveryId);
        Assert.NotNull(retrieved1);
        Assert.Equal(SourceEncodingMode.ForceWindows1251, retrieved1.Value.SourceMode);
        Assert.Equal(TargetDicomEncoding.IsoIr192, retrieved1.Value.TargetEncoding);

        // Simulate app restart with a new Database instance
        var db2 = new Database(dbPath);
        var retrieved2 = db2.GetDeliveryEncodingOverride(testDeliveryId);
        Assert.NotNull(retrieved2);
        Assert.Equal(SourceEncodingMode.ForceWindows1251, retrieved2.Value.SourceMode);
        Assert.Equal(TargetDicomEncoding.IsoIr192, retrieved2.Value.TargetEncoding);

        // Remove override
        db2.RemoveDeliveryEncodingOverride(testDeliveryId);
        Assert.Null(db2.GetDeliveryEncodingOverride(testDeliveryId));
    }

    [Fact]
    public async Task EncodingProblem_RemainsActiveAfterFailedCStore_AndResolvesAfterSuccessfulCStore()
    {
        var dbPath = Path.Combine(_dir, "problem_lifecycle.db");
        var logPath = Path.Combine(_dir, "app.log");
        var db = new Database(dbPath);
        var logger = new AppLogger(logPath);

        var pacs = new PacsSettings { Id = "pacs-1", Name = "PACS 1", Enabled = true };
        var folder = new WatchFolderSettings { Id = "folder-1", Name = "Folder 1", Path = _dir, PacsIds = [pacs.Id], Enabled = true };

        var settings = new AppSettings
        {
            DatabaseFile = dbPath,
            LogFile = logPath,
            WatchFolders = [folder],
            PacsServers = [pacs],
            EncodingRules = [],
            MaxSendAttempts = 3
        };

        var path = Path.Combine(_dir, "test_file.dcm");
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 100" },
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, "Иванов^Иван" }
        };
        new DicomFile(dataset).Save(path);

        db.Enqueue("key-problem", path, "1.2.3.100", "Иванов", folder.Id, [pacs]);
        var delivery = db.GetNextReadyDelivery()!;

        // 1. First attempt: mock sender returns ProcessingFailure (отказ PACS)
        var monitor = new FolderMonitor(settings, db, logger)
        {
            PacsEchoChecker = (_, _) => Task.FromResult(true),
            DicomSender = (_, _, _) => Task.FromResult<DicomStatus?>(DicomStatus.ProcessingFailure)
        };

        // Engineer registers one-time override in DB
        db.SetDeliveryEncodingOverride(delivery.Id, SourceEncodingMode.ForceWindows1251, TargetDicomEncoding.IsoIr192);

        // Manually record problem (as would be created on failure)
        db.ReportProblem($"delivery:{delivery.Id}", "Encoding", "test_file.dcm", "Ошибка кодировки", path, delivery.QueueItemId, pacs.Id);
        Assert.Contains(db.GetActiveProblems(), p => p.Key == $"delivery:{delivery.Id}");

        // Execute send which fails
        await monitor.SendAsync(delivery, CancellationToken.None);

        // PROBLEM MUST REMAIN ACTIVE AFTER FAILED C-STORE!
        var activeProblemsAfterFailure = db.GetActiveProblems();
        Assert.Contains(activeProblemsAfterFailure, p => p.Key == $"delivery:{delivery.Id}");

        // ONE-TIME OVERRIDE MUST REMAIN IN DB AFTER FAILED C-STORE!
        Assert.NotNull(db.GetDeliveryEncodingOverride(delivery.Id));

        // 2. Second attempt: C-STORE succeeds!
        monitor.DicomSender = (_, _, _) => Task.FromResult<DicomStatus?>(DicomStatus.Success);
        db.RetryDelivery(delivery.Id);
        var retriedDelivery = db.GetNextReadyDelivery()!;

        await monitor.SendAsync(retriedDelivery, CancellationToken.None);

        // PROBLEM MUST BE RESOLVED AFTER SUCCESSFUL C-STORE!
        var activeProblemsAfterSuccess = db.GetActiveProblems();
        Assert.DoesNotContain(activeProblemsAfterSuccess, p => p.Key == $"delivery:{delivery.Id}");

        // ONE-TIME OVERRIDE MUST BE DELETED AFTER SUCCESSFUL C-STORE!
        Assert.Null(db.GetDeliveryEncodingOverride(delivery.Id));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
