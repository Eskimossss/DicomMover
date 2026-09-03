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
            var pacs = new PacsSettings { Id = "pacs" };
            var settings = new AppSettings
            {
                PacsServers = [pacs],
                WatchFolders = [new WatchFolderSettings { Name = "Root", Path = directory, PacsIds = [pacs.Id] }]
            };
            store.Save(settings);
            settings.ScanIntervalSeconds = 90;
            store.Save(settings);

            Assert.Equal(90, store.Load().ScanIntervalSeconds);
            Assert.True(File.Exists(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReturnsEmptyConfigurationOnFirstRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DicomMoverSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "appsettings.json");

        var settings = new SettingsStore(path).Load();

        Assert.Empty(settings.WatchFolders);
        Assert.Empty(settings.PacsServers);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LoadsExistingConfigurationWithoutEncodingFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DicomMoverSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appsettings.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "WatchFolders": [{ "Id": "f", "Name": "Папка", "Path": "C:\\\\dicom", "Enabled": true, "PacsIds": ["p"] }],
                  "PacsServers": [{ "Id": "p", "Name": "PACS", "Enabled": true, "IpAddress": "127.0.0.1", "Port": 104, "CalledAeTitle": "PACS", "CallingAeTitle": "MOVER" }]
                }
                """);
            var settings = new SettingsStore(path).Load();
            Assert.Empty(settings.EncodingRules);
            Assert.InRange(settings.JournalHeight, 120, 600);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingSourceEncodingMigratesToAutomaticAndRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DicomMoverSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appsettings.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "WatchFolders": [{ "Id": "f", "Name": "Папка", "Path": "C:\\dicom", "Enabled": true, "PacsIds": ["p"] }],
                  "PacsServers": [{ "Id": "p", "Name": "PACS", "Enabled": true, "IpAddress": "127.0.0.1", "Port": 104, "CalledAeTitle": "PACS", "CallingAeTitle": "MOVER" }],
                  "EncodingRules": [{ "Name": "Старое правило", "DestinationPacsId": "p", "SourceFolderId": "f", "ProcessingMode": "RepairInvalidTextElements", "TargetEncoding": "IsoIr192", "SelectedTextFields": ["00100010"] }]
                }
                """);
            var store = new SettingsStore(path);
            var settings = store.Load();
            Assert.Equal(SourceEncodingMode.Automatic, Assert.Single(settings.EncodingRules).SourceEncodingMode);

            settings.EncodingRules[0].SourceEncodingMode = SourceEncodingMode.ForceIso88595;
            store.Save(settings);
            Assert.Equal(SourceEncodingMode.ForceIso88595, Assert.Single(store.Load().EncodingRules).SourceEncodingMode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AssemblyVersion_And_AboutDisplayVersion_Is1_5()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var version = assembly.GetName().Version!;
        var infoVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        var displayVersion = !string.IsNullOrWhiteSpace(infoVersion)
            ? infoVersion.Split('+')[0].Trim()
            : version.ToString(2);

        Assert.Equal("1.5.0.0", version.ToString());
        Assert.Equal("1.5", infoVersion);
        Assert.Equal("1.5", displayVersion);
    }
}
