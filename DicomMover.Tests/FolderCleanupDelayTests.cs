using DicomMover.Models;
using Xunit;

namespace DicomMover.Tests;

public sealed class FolderCleanupDelayTests
{
    [Fact]
    public void DefaultValueIs30Minutes()
    {
        var folder = new WatchFolderSettings();
        Assert.Equal(30, folder.EmptyFolderCleanupDelayMinutes);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(1440)]
    public void AllowedRangeIsValidated(int minutes)
    {
        var pacs = new PacsSettings { Id = "pacs1" };
        var folder = new WatchFolderSettings
        {
            Id = "f1",
            Name = "Folder",
            Path = @"C:\Dicom",
            PacsIds = [pacs.Id],
            DeleteEmptySubfolders = true,
            EmptyFolderCleanupDelayMinutes = minutes
        };
        var settings = new AppSettings
        {
            PacsServers = [pacs],
            WatchFolders = [folder]
        };

        // Should not throw
        settings.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(1441)]
    [InlineData(-10)]
    public void OutOfRangeValuesThrowValidationException(int minutes)
    {
        var pacs = new PacsSettings { Id = "pacs1" };
        var folder = new WatchFolderSettings
        {
            Id = "f1",
            Name = "Folder",
            Path = @"C:\Dicom",
            PacsIds = [pacs.Id],
            DeleteEmptySubfolders = true,
            EmptyFolderCleanupDelayMinutes = minutes
        };
        var settings = new AppSettings
        {
            PacsServers = [pacs],
            WatchFolders = [folder]
        };

        var ex = Assert.Throws<InvalidDataException>(() => settings.Validate());
        Assert.Contains("от 5 до 1440", ex.Message);
    }

    [Fact]
    public void TogglingDeleteEmptySubfoldersPreservesSavedValue()
    {
        var folder = new WatchFolderSettings
        {
            EmptyFolderCleanupDelayMinutes = 45,
            DeleteEmptySubfolders = true
        };

        Assert.Equal(45, folder.EmptyFolderCleanupDelayMinutes);

        // Turn off
        folder.DeleteEmptySubfolders = false;
        Assert.Equal(45, folder.EmptyFolderCleanupDelayMinutes);

        // Turn back on
        folder.DeleteEmptySubfolders = true;
        Assert.Equal(45, folder.EmptyFolderCleanupDelayMinutes);
    }
}
