using System.Text.Json;
using DicomMover.Models;
using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void MigratesLegacySingleFolderAndPacs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DicomMoverSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appsettings.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                WatchFolder = @"C:\legacy",
                SearchSubfolders = true,
                RemoteHost = "192.168.1.10",
                RemotePort = 4242,
                RemoteAeTitle = "ARCHIVE",
                LocalAeTitle = "MOVER"
            }));

            var settings = new SettingsStore(path).Load();

            Assert.Equal(@"C:\legacy", Assert.Single(settings.WatchFolders).Path);
            Assert.Equal("192.168.1.10", Assert.Single(settings.PacsServers).IpAddress);
            Assert.Single(settings.WatchFolders[0].PacsIds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SavesSettingsAtomicallyAndCreatesBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DicomMoverSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appsettings.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.NormalizeLegacy();
            store.Save(settings);
            settings.ScanIntervalSeconds = 90;
            store.Save(settings);

            Assert.Equal(90, store.Load().ScanIntervalSeconds);
            Assert.True(File.Exists(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
