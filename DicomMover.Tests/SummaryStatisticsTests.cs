using DicomMover.Models;
using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class SummaryStatisticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dicommover_summary_tests_" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "summary_test.db");

    public SummaryStatisticsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
        catch { }
    }

    [Fact]
    public void FourStudiesOnPacs1_AndOneAlsoOnPacs2_ComputesExactDeliveriesAndUniqueStudies()
    {
        var database = new Database(DatabasePath);
        var pacs1 = new PacsSettings { Id = "pacs-1", Name = "PACS 1", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS1" };
        var pacs2 = new PacsSettings { Id = "pacs-2", Name = "PACS 2", IpAddress = "10.0.0.2", Port = 104, CalledAeTitle = "PACS2" };

        // 4 studies: study1..study3 sent to PACS 1; study4 sent to both PACS 1 and PACS 2
        database.Enqueue("k1", "file1.dcm", "1.1", null, "f1", [pacs1], new DicomMetadata { SopInstanceUid = "1.1", PatientName = "Patient 1" });
        database.Enqueue("k2", "file2.dcm", "1.2", null, "f1", [pacs1], new DicomMetadata { SopInstanceUid = "1.2", PatientName = "Patient 2" });
        database.Enqueue("k3", "file3.dcm", "1.3", null, "f1", [pacs1], new DicomMetadata { SopInstanceUid = "1.3", PatientName = "Patient 3" });
        database.Enqueue("k4", "file4.dcm", "1.4", null, "f1", [pacs1, pacs2], new DicomMetadata { SopInstanceUid = "1.4", PatientName = "Patient 4" });

        // Process all 5 deliveries and mark them as Sent
        for (var i = 0; i < 5; i++)
        {
            var delivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
            database.MarkDeliverySending(delivery);
            database.MarkDeliverySent(delivery, "Success");
        }

        // Verify "Все PACS" summary
        var allSummary = database.GetPeriodSummary(DateTime.Today, DateTime.Today);
        Assert.Equal(4, allSummary.Found);      // 4 unique studies
        Assert.Equal(5, allSummary.Sent);       // 5 successful sends
        Assert.Equal(0, allSummary.WithErrors); // 0 not sent
        Assert.Equal(5, allSummary.Attempts);   // 5 total attempts

        var allPacsStats = database.GetPacsPeriodSummary(DateTime.Today, DateTime.Today);
        Assert.Equal(2, allPacsStats.Count);
        var p1Stats = Assert.Single(allPacsStats, p => p.PacsName == "PACS 1");
        Assert.Equal(4, p1Stats.Assigned);
        Assert.Equal(4, p1Stats.Sent);
        Assert.Equal(0, p1Stats.WithErrors);
        Assert.Equal(4, p1Stats.Attempts);

        var p2Stats = Assert.Single(allPacsStats, p => p.PacsName == "PACS 2");
        Assert.Equal(1, p2Stats.Assigned);
        Assert.Equal(1, p2Stats.Sent);
        Assert.Equal(0, p2Stats.WithErrors);
        Assert.Equal(1, p2Stats.Attempts);

        var allFileResults = database.GetFileResultsForPeriod(DateTime.Today, DateTime.Today);
        Assert.Equal(5, allFileResults.Count);
        Assert.Equal(4, allFileResults.Count(r => r.PacsName == "PACS 1"));
        Assert.Equal(1, allFileResults.Count(r => r.PacsName == "PACS 2"));
        // Study 4 appears in two separate rows
        Assert.Equal(2, allFileResults.Count(r => r.FileName == "file4.dcm"));

        // Verify PACS 1 filter
        var pacs1Summary = database.GetPeriodSummary(DateTime.Today, DateTime.Today, pacs1.Id);
        Assert.Equal(4, pacs1Summary.Found);
        Assert.Equal(4, pacs1Summary.Sent);
        Assert.Equal(0, pacs1Summary.WithErrors);
        Assert.Equal(4, pacs1Summary.Attempts);

        var pacs1OnlyStats = database.GetPacsPeriodSummary(DateTime.Today, DateTime.Today, pacs1.Id);
        Assert.Single(pacs1OnlyStats);
        Assert.Equal("PACS 1", pacs1OnlyStats[0].PacsName);

        var pacs1FileResults = database.GetFileResultsForPeriod(DateTime.Today, DateTime.Today, pacs1.Id);
        Assert.Equal(4, pacs1FileResults.Count);
        Assert.All(pacs1FileResults, r => Assert.Equal("PACS 1", r.PacsName));

        // Verify PACS 2 filter
        var pacs2Summary = database.GetPeriodSummary(DateTime.Today, DateTime.Today, pacs2.Id);
        Assert.Equal(1, pacs2Summary.Found);
        Assert.Equal(1, pacs2Summary.Sent);
        Assert.Equal(0, pacs2Summary.WithErrors);
        Assert.Equal(1, pacs2Summary.Attempts);

        var pacs2OnlyStats = database.GetPacsPeriodSummary(DateTime.Today, DateTime.Today, pacs2.Id);
        Assert.Single(pacs2OnlyStats);
        Assert.Equal("PACS 2", pacs2OnlyStats[0].PacsName);

        var pacs2FileResults = database.GetFileResultsForPeriod(DateTime.Today, DateTime.Today, pacs2.Id);
        Assert.Single(pacs2FileResults);
        Assert.Equal("file4.dcm", pacs2FileResults[0].FileName);
        Assert.Equal("PACS 2", pacs2FileResults[0].PacsName);
    }

    [Fact]
    public void Summary_TracksFailedDeliveriesAndMultipleAttempts()
    {
        var database = new Database(DatabasePath);
        var pacs = new PacsSettings { Id = "pacs-1", Name = "PACS 1" };

        // Study 1 fails after 2 attempts
        database.Enqueue("k1", "fail.dcm", "1.1", null, "f1", [pacs]);
        var d1 = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        database.MarkDeliverySending(d1);
        database.MarkDeliveryFailed(d1, "Network error 1", DateTime.UtcNow.AddMinutes(5), false);
        database.MarkDeliverySending(d1);
        database.MarkDeliveryFailed(d1, "Network error 2", null, true);

        // Study 2 succeeds on 3rd attempt
        database.Enqueue("k2", "ok.dcm", "1.2", null, "f1", [pacs]);
        var d2 = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        database.MarkDeliverySending(d2);
        database.MarkDeliveryFailed(d2, "Temporary error 1", DateTime.UtcNow.AddMinutes(5), false);
        database.MarkDeliverySending(d2);
        database.MarkDeliveryFailed(d2, "Temporary error 2", DateTime.UtcNow.AddMinutes(5), false);
        database.MarkDeliverySending(d2);
        database.MarkDeliverySent(d2, "Success");

        var summary = database.GetPeriodSummary(DateTime.Today, DateTime.Today, pacs.Id);
        Assert.Equal(2, summary.Found);
        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.WithErrors);
        Assert.Equal(5, summary.Attempts); // 2 attempts on d1 + 3 attempts on d2 = 5
    }

    [Fact]
    public void Summary_FilterByDate_WorksJointlyWithPacsFilter()
    {
        var database = new Database(DatabasePath);
        var pacs = new PacsSettings { Id = "pacs-1", Name = "PACS 1" };
        database.Enqueue("k1", "file.dcm", "1.1", null, "f1", [pacs]);

        var delivery = Assert.IsType<DeliveryItem>(database.GetNextReadyDelivery());
        database.MarkDeliverySending(delivery);
        database.MarkDeliverySent(delivery, "Success");

        // Query today: should find it
        var todaySummary = database.GetPeriodSummary(DateTime.Today, DateTime.Today, pacs.Id);
        Assert.Equal(1, todaySummary.Found);
        Assert.Equal(1, todaySummary.Sent);

        // Query yesterday: should not find it
        var yesterdaySummary = database.GetPeriodSummary(DateTime.Today.AddDays(-2), DateTime.Today.AddDays(-1), pacs.Id);
        Assert.Equal(0, yesterdaySummary.Found);
        Assert.Equal(0, yesterdaySummary.Sent);
    }
}
