using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Xunit;

namespace DicomMover.Tests;

public sealed class DicomMetadataUxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DicomMoverMetadataTests-" + Guid.NewGuid().ToString("N"));

    public DicomMetadataUxTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DiscoveryCollectsAndSortsAllSupportedValues()
    {
        CreateDicom("a.dcm", "MG", "PINKVIEW", "ACME");
        CreateDicom("b.dcm", "DX", "XRAY_ROOM", "GE MEDICAL SYSTEMS");

        var values = Discover();

        Assert.Equal(["DX", "MG"], values.Modalities);
        Assert.Equal(["PINKVIEW", "XRAY_ROOM"], values.StationNames);
        Assert.Equal(["ACME", "GE MEDICAL SYSTEMS"], values.Manufacturers);
        Assert.Equal(2, values.FilesAnalyzed);
    }

    [Fact]
    public void UniqueValuesTrimIgnoreCaseAndSkipMissingTags()
    {
        CreateDicom("a.dcm", "MG", " PINKVIEW ", "ACME");
        CreateDicom("b.dcm", "MG", null, "acme");
        CreateDicom("c.dcm", null, "pinkview", " ");

        var values = Discover();

        Assert.Single(values.Modalities);
        Assert.Equal("MG", values.Modalities[0], ignoreCase: true);
        Assert.Single(values.StationNames);
        Assert.Equal("PINKVIEW", values.StationNames[0], ignoreCase: true);
        Assert.Single(values.Manufacturers);
    }

    [Fact]
    public void EmptyFolderReturnsEmptyLists()
    {
        var values = Discover();
        Assert.Empty(values.Modalities);
        Assert.Empty(values.StationNames);
        Assert.Empty(values.Manufacturers);
        Assert.Equal(0, values.FilesAnalyzed);
    }

    [Fact]
    public void DiscoveryReadsMetadataFromFileWithLargePixelData()
    {
        CreateDicom("large.dcm", "MG", "PINKVIEW", "ACME", pixelBytes: 5 * 1024 * 1024);
        var values = Discover();
        Assert.Equal("MG", Assert.Single(values.Modalities));
        Assert.Equal("PINKVIEW", Assert.Single(values.StationNames));
    }

    [Fact]
    public void RuleConditionValueRoundTripsThroughSettingsJson()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = ValidSettings();
        settings.EncodingRules = [new EncodingRule
        {
            Name = "MG", SourceFolderId = "folder", DestinationPacsId = "pacs",
            MatchModality = true, Modality = "MG", MatchStationName = true, StationName = "PINKVIEW"
        }];

        var store = new SettingsStore(path);
        store.Save(settings);
        var rule = Assert.Single(store.Load().EncodingRules);
        Assert.Equal("MG", rule.Modality);
        Assert.Equal("PINKVIEW", rule.StationName);
    }

    [Fact]
    public void DiagnosticFiltersCombineModalityStationAndProblems()
    {
        var files = new[]
        {
            Diagnostic("mg-problem", "MG", "PINKVIEW", problem: true),
            Diagnostic("mg-ok", "MG", "PINKVIEW", problem: false),
            Diagnostic("dx-problem", "DX", "PINKVIEW", problem: true),
            Diagnostic("other", "MG", "OTHER", problem: true),
            Diagnostic("missing", "MG", null, problem: true)
        };

        Assert.Equal(4, DicomResultFilter.Diagnostics(files, 0, "MG", null).Count);
        Assert.Equal(3, DicomResultFilter.Diagnostics(files, 0, null, "PINKVIEW").Count);
        Assert.Equal(2, DicomResultFilter.Diagnostics(files, 0, "MG", "PINKVIEW").Count);
        Assert.Equal("mg-problem", Assert.Single(DicomResultFilter.Diagnostics(files, 1, "MG", "PINKVIEW")).FilePath);
    }

    [Fact]
    public void ConversionFiltersUseSameMetadataRules()
    {
        var files = new[]
        {
            Conversion("one", "MG", "PINKVIEW"),
            Conversion("two", "DX", "PINKVIEW"),
            Conversion("three", "MG", "OTHER")
        };

        Assert.Equal("one", Assert.Single(DicomResultFilter.Conversions(files, 0, "mg", "pinkview")).FilePath);
    }

    [Fact]
    public void SelectionIsPreservedOrMovesToFirstVisibleFileAndPositionIsRecalculated()
    {
        var files = new[]
        {
            Diagnostic("one", "MG", "A", false),
            Diagnostic("two", "MG", "A", false),
            Diagnostic("three", "DX", "A", false)
        };
        var mg = DicomResultFilter.Diagnostics(files, 0, "MG", null);

        Assert.Equal(2, mg.Count);
        Assert.Equal("two", DicomResultFilter.PreserveSelection(mg, "two", x => x.FilePath)?.FilePath);
        Assert.Equal("one", DicomResultFilter.PreserveSelection(mg, "three", x => x.FilePath)?.FilePath);
        Assert.Equal(1, mg.IndexOf(DicomResultFilter.PreserveSelection(mg, "two", x => x.FilePath)!));
    }

    private DicomMetadataValues Discover() => new DicomMetadataDiscoveryService().Discover(new WatchFolderSettings
    {
        Id = "folder", Path = _directory, SearchSubfolders = false
    });

    private void CreateDicom(string name, string? modality, string? station, string? manufacturer, int pixelBytes = 1)
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.Rows, (ushort)1 }, { DicomTag.Columns, (ushort)1 },
            { DicomTag.SamplesPerPixel, (ushort)1 }, { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            { DicomTag.BitsAllocated, (ushort)8 }, { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 }, { DicomTag.PixelRepresentation, (ushort)0 }
        };
        if (modality is not null) dataset.AddOrUpdate(DicomTag.Modality, modality);
        if (station is not null) dataset.AddOrUpdate(DicomTag.StationName, station);
        if (manufacturer is not null) dataset.AddOrUpdate(DicomTag.Manufacturer, manufacturer);
        dataset.Add(new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer(new byte[pixelBytes])));
        new DicomFile(dataset).Save(Path.Combine(_directory, name));
    }

    private static DicomFileDiagnosticResult Diagnostic(string path, string? modality, string? station, bool problem)
    {
        var elements = problem ? new[] { Element(DicomTextElementStatus.Ambiguous) } : new[] { Element(DicomTextElementStatus.Valid) };
        return new(path, path, elements, Metadata: new(modality, station, null));
    }

    private static DicomFileConversionResult Conversion(string path, string? modality, string? station) => new(path,
        new(path, "ISO_IR 144", "ISO_IR 192", [], "✓ Преобразование безопасно."),
        Metadata: new(modality, station, null));

    private static DicomTextElementAnalysis Element(DicomTextElementStatus status) => new(
        "file", "path", "(0010,0010)", "00100010", "Patient Name", "PN", "ISO_IR 192", status,
        "value", null, null, "", "", "", "", true, "test");

    private static AppSettings ValidSettings() => new()
    {
        WatchFolders = [new() { Id = "folder", Name = "Folder", Path = @"C:\input", PacsIds = ["pacs"] }],
        PacsServers = [new() { Id = "pacs", Name = "PACS", IpAddress = "127.0.0.1", CalledAeTitle = "PACS", CallingAeTitle = "MOVER" }]
    };

    public void Dispose() => Directory.Delete(_directory, true);
}
