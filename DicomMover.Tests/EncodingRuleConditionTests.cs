using System.Text.Json;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using Xunit;

namespace DicomMover.Tests;

public sealed class EncodingRuleConditionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DicomMoverRuleTests-" + Guid.NewGuid().ToString("N"));

    public EncodingRuleConditionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void RuleWithoutConditionsMatchesLikeLegacyRule()
    {
        var rule = Rule();
        Assert.Same(rule, Resolve([rule], new("DX", "OTHER", "VENDOR")));
    }

    [Theory]
    [InlineData("MG", true)]
    [InlineData("mg", true)]
    [InlineData("  MG  ", true)]
    [InlineData("DX", false)]
    [InlineData(null, false)]
    public void ModalityUsesTrimmedCaseInsensitiveExactMatch(string? modality, bool expected)
    {
        var rule = Rule(); rule.MatchModality = true; rule.Modality = " mg ";
        Assert.Equal(expected, Resolve([rule], new(modality, null, null)) is not null);
    }

    [Fact]
    public void MultipleConditionsUseAndAndMissingTagDoesNotMatch()
    {
        var rule = Rule();
        rule.MatchModality = true; rule.Modality = "MG";
        rule.MatchStationName = true; rule.StationName = "PINKVIEW";

        Assert.Same(rule, Resolve([rule], new("MG", "pinkview", null)));
        Assert.Null(Resolve([rule], new("MG", "OTHER", null)));
        Assert.Null(Resolve([rule], new("MG", null, null)));
    }

    [Fact]
    public void ManufacturerAndAllThreeConditionsAreSupported()
    {
        var rule = Rule();
        rule.MatchModality = true; rule.Modality = "MG";
        rule.MatchStationName = true; rule.StationName = "PINKVIEW";
        rule.MatchManufacturer = true; rule.Manufacturer = "ACME";

        Assert.Same(rule, Resolve([rule], new("MG", "PINKVIEW", "acme")));
        Assert.Null(Resolve([rule], new("MG", "PINKVIEW", "OTHER")));
    }

    [Fact]
    public void TwoRulesForSameRouteSelectTheirOwnModality()
    {
        var mg = Rule("MG"); mg.MatchModality = true; mg.Modality = "MG";
        var dx = Rule("DX"); dx.MatchModality = true; dx.Modality = "DX";

        Assert.Same(mg, Resolve([mg, dx], new("MG", null, null)));
        Assert.Same(dx, Resolve([mg, dx], new("DX", null, null)));
    }

    [Fact]
    public void MoreSpecificRuleWinsAndEqualSpecificityUsesStableOrder()
    {
        var fallback = Rule("Fallback");
        var modality = Rule("Modality"); modality.MatchModality = true; modality.Modality = "MG";
        var exact = Rule("Exact"); exact.MatchModality = true; exact.Modality = "MG"; exact.MatchStationName = true; exact.StationName = "PINKVIEW";
        var sameSpecificity = Rule("Same"); sameSpecificity.MatchManufacturer = true; sameSpecificity.Manufacturer = "ACME"; sameSpecificity.MatchStationName = true; sameSpecificity.StationName = "PINKVIEW";

        Assert.Same(exact, Resolve([fallback, exact, sameSpecificity, modality], new("MG", "PINKVIEW", "ACME")));
    }

    [Fact]
    public void MissingConditionPropertiesInOldJsonRemainDisabled()
    {
        var rule = JsonSerializer.Deserialize<EncodingRule>("""
            { "Name":"Старое", "SourceFolderId":"folder", "DestinationPacsId":"pacs" }
            """)!;

        Assert.False(rule.MatchModality);
        Assert.False(rule.MatchStationName);
        Assert.False(rule.MatchManufacturer);
        Assert.Same(rule, Resolve([rule], new(null, null, null)));
    }

    [Fact]
    public void ValidationAllowsDistinctConditionsButRejectsEmptyEnabledCondition()
    {
        var settings = ValidSettings();
        var mg = Rule("MG"); mg.MatchModality = true; mg.Modality = "MG";
        var dx = Rule("DX"); dx.MatchModality = true; dx.Modality = "DX";
        settings.EncodingRules = [mg, dx];
        settings.Validate();

        dx.Modality = " ";
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void ResolveForFileReadsMetadataWithoutPixelDataAndNoMatchLeavesFileUntouched()
    {
        var path = CreateDicom("DX", "PINKVIEW", "ACME");
        var before = File.ReadAllBytes(path);
        var rule = Rule(); rule.MatchModality = true; rule.Modality = "MG";

        Assert.Null(new EncodingRuleResolver().ResolveForFile([rule], "folder", "pacs", path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void PreviewDoesNotTranscodeOrReportErrorWhenRuleDoesNotMatch()
    {
        var path = CreateDicom("DX", "PINKVIEW", "ACME");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-5));
        var before = File.ReadAllBytes(path);
        var rule = Rule(); rule.MatchModality = true; rule.Modality = "MG";
        var folder = new WatchFolderSettings { Id = "folder", Path = _directory, SearchSubfolders = false };

        var result = Assert.Single(new DicomTextDiagnosticService().PreviewFolderFiles(folder, rule));

        Assert.False(result.IsApplicable);
        Assert.False(result.HasProblem);
        Assert.Contains("Правило не применяется", result.Preview.Result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private EncodingRule? Resolve(IEnumerable<EncodingRule> rules, DicomRuleMetadata metadata) =>
        new EncodingRuleResolver().Resolve(rules, "folder", "pacs", metadata);

    private static EncodingRule Rule(string name = "Rule") => new()
    {
        Name = name, Enabled = true, SourceFolderId = "folder", DestinationPacsId = "pacs",
        ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements
    };

    private static AppSettings ValidSettings() => new()
    {
        WatchFolders = [new() { Id = "folder", Name = "Folder", Path = @"C:\input", PacsIds = ["pacs"] }],
        PacsServers = [new() { Id = "pacs", Name = "PACS", IpAddress = "127.0.0.1", CalledAeTitle = "PACS", CallingAeTitle = "MOVER" }]
    };

    private string CreateDicom(string modality, string station, string manufacturer)
    {
        var path = Path.Combine(_directory, "metadata.dcm");
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.Modality, modality },
            { DicomTag.StationName, station },
            { DicomTag.Manufacturer, manufacturer },
            { DicomTag.Rows, (ushort)1 }, { DicomTag.Columns, (ushort)1 },
            { DicomTag.SamplesPerPixel, (ushort)1 }, { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            { DicomTag.BitsAllocated, (ushort)8 }, { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 }, { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.PixelData, new byte[] { 42 } }
        };
        new DicomFile(dataset).Save(path);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
