using DicomMover.Models;
using Xunit;

namespace DicomMover.Tests;

public sealed class PacsDuplicateTests
{
    [Fact]
    public void EndpointDuplicate_Detected_WhenExactMatch()
    {
        var p1 = new PacsSettings { Name = "Main PACS", IpAddress = "192.168.1.50", Port = 104, CalledAeTitle = "ARCHIVE" };
        var p2 = new PacsSettings { Name = "Copy PACS", IpAddress = "192.168.1.50", Port = 104, CalledAeTitle = "ARCHIVE" };

        Assert.True(PacsSettings.IsEndpointDuplicate(p1, p2));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Assertions", "xUnit2005:Do not use identity check on value type")]
    public void EndpointDuplicate_Detected_CaseInsensitive()
    {
        var p1 = new PacsSettings { Name = "PACS 1", IpAddress = "pacs.hospital.local", Port = 11112, CalledAeTitle = "orthanc" };
        var p2 = new PacsSettings { Name = "PACS 2", IpAddress = "PACS.HOSPITAL.LOCAL", Port = 11112, CalledAeTitle = "ORTHANC" };

        Assert.True(PacsSettings.IsEndpointDuplicate(p1, p2));
    }

    [Fact]
    public void EndpointDuplicate_Detected_WithLeadingAndTrailingWhitespace()
    {
        var p1 = new PacsSettings { Name = "PACS 1", IpAddress = "  10.0.0.1  ", Port = 104, CalledAeTitle = "  PACS  " };
        var p2 = new PacsSettings { Name = "PACS 2", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };

        Assert.True(PacsSettings.IsEndpointDuplicate(p1, p2));
    }

    [Fact]
    public void EndpointDuplicate_NotDetected_WhenPortDiffers()
    {
        var p1 = new PacsSettings { Name = "PACS 1", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };
        var p2 = new PacsSettings { Name = "PACS 2", IpAddress = "10.0.0.1", Port = 11112, CalledAeTitle = "PACS" };

        Assert.False(PacsSettings.IsEndpointDuplicate(p1, p2));
    }

    [Fact]
    public void EndpointDuplicate_NotDetected_WhenCalledAeDiffers()
    {
        var p1 = new PacsSettings { Name = "PACS 1", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS_A" };
        var p2 = new PacsSettings { Name = "PACS 2", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS_B" };

        Assert.False(PacsSettings.IsEndpointDuplicate(p1, p2));
    }

    [Fact]
    public void Validate_ThrowsExpectedMessage_WhenDuplicatePacsExists()
    {
        var p1 = new PacsSettings { Id = "p1", Name = "Главный архив", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };
        var p2 = new PacsSettings { Id = "p2", Name = "Второй архив", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };

        var settings = new AppSettings
        {
            PacsServers = [p1, p2],
            WatchFolders = [new WatchFolderSettings { PacsIds = ["p1"] }]
        };

        var ex = Assert.Throws<InvalidDataException>(() => settings.Validate());
        Assert.Equal("PACS с такими параметрами уже существует: «Главный архив». Измените AE Title, адрес или порт.", ex.Message);
    }

    [Fact]
    public void FindDuplicatePacs_ExcludesEditingPacsById()
    {
        var p1 = new PacsSettings { Id = "p1", Name = "Архив 1", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };
        var settings = new AppSettings { PacsServers = [p1] };

        // Editing p1 without changing endpoint should not report duplicate with itself
        var duplicate = settings.FindDuplicatePacs(p1, excludeId: p1.Id);
        Assert.Null(duplicate);
    }

    [Fact]
    public void FindDuplicatePacs_ReportsDuplicate_WhenEditingToMatchAnotherPacs()
    {
        var p1 = new PacsSettings { Id = "p1", Name = "Архив 1", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };
        var p2 = new PacsSettings { Id = "p2", Name = "Архив 2", IpAddress = "10.0.0.2", Port = 104, CalledAeTitle = "PACS" };
        var settings = new AppSettings { PacsServers = [p1, p2] };

        // Editing p2 to point to 10.0.0.1
        var candidate = new PacsSettings { Id = "p2", Name = "Архив 2", IpAddress = "10.0.0.1", Port = 104, CalledAeTitle = "PACS" };
        var duplicate = settings.FindDuplicatePacs(candidate, excludeId: "p2");

        Assert.NotNull(duplicate);
        Assert.Equal("Архив 1", duplicate.Name);
    }
}
