using System.Security.Cryptography;
using System.Text;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Xunit;

namespace DicomMover.Tests;

public sealed class DicomEncodingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DicomMoverEncodingTests-" + Guid.NewGuid().ToString("N"));

    public DicomEncodingTests()
    {
        Directory.CreateDirectory(_directory);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DicomEncoding.RegisterEncoding("CP1251TEST", "windows-1251");
    }

    [Fact]
    public void ResolverUsesOnlyRuleForSpecificFolder()
    {
        var resolver = new EncodingRuleResolver();
        Assert.Null(resolver.Resolve([], "folder", "pacs"));
        var any = Rule(null, "pacs");
        var exact = Rule("folder", "pacs");
        Assert.Same(exact, resolver.Resolve([any, exact], "folder", "pacs"));
        Assert.Null(resolver.Resolve([any, exact], "other", "pacs"));
    }

    [Fact]
    public void SettingsRejectDuplicateEnabledRulesButAllowTwoFoldersForOnePacs()
    {
        var settings = ValidSettings();
        settings.EncodingRules = [Rule("f1", "p1"), Rule("f1", "p1")];
        Assert.Throws<InvalidDataException>(settings.Validate);
        settings.EncodingRules = [Rule("f1", "p1"), Rule("f2", "p1")];
        settings.Validate();
    }

    [Fact]
    public void LegacyAnyFolderRuleMigratesToFirstConfiguredFolder()
    {
        var settings = ValidSettings();
        settings.EncodingRules = [Rule(null, "p1")];

        settings.NormalizeLegacy();

        Assert.Equal("f1", settings.EncodingRules[0].SourceFolderId);
        Assert.Same(settings.EncodingRules[0], new EncodingRuleResolver().Resolve(settings.EncodingRules, "f1", "p1"));
        Assert.Null(new EncodingRuleResolver().Resolve(settings.EncodingRules, "f2", "p1"));
    }

    [Theory]
    [InlineData(TargetDicomEncoding.IsoIr192, "ISO_IR 192")]
    [InlineData(TargetDicomEncoding.IsoIr144, "ISO_IR 144")]
    public void EmptyCharsetCp1251TranscodesWithoutChangingSourceOrPixelData(TargetDicomEncoding target, string expectedCharset)
    {
        var path = CreateCp1251File(emptyCharset: true, wrongDeclaredCharset: false, patientName: "Иванов^Иван");
        var hashBefore = SHA256.HashData(File.ReadAllBytes(path));
        var originalPixel = DicomFile.Open(path, Encoding.GetEncoding(1251), stop: null, FileReadOption.ReadAll)
            .Dataset.GetDicomItem<DicomOtherByte>(DicomTag.PixelData)!.Buffer.Data;

        var result = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            SourceEncodingMode = SourceEncodingMode.UseFallbackWhenCharsetEmpty,
            EmptyCharsetFallback = EmptyCharsetFallback.Windows1251,
            TargetEncoding = target
        });

        Assert.Equal(expectedCharset, result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
        Assert.Equal("Иванов^Иван", result.File.Dataset.GetString(DicomTag.PatientName));
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(originalPixel, result.File.Dataset.GetDicomItem<DicomOtherByte>(DicomTag.PixelData)!.Buffer.Data);
    }

    [Fact]
    public void MissingCharsetCp1251UsesFallback()
    {
        var path = CreateCp1251File(emptyCharset: false, wrongDeclaredCharset: false, patientName: "Иванова^Анна", removeCharset: true);
        var result = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            SourceEncodingMode = SourceEncodingMode.UseFallbackWhenCharsetEmpty,
            EmptyCharsetFallback = EmptyCharsetFallback.Windows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        });
        Assert.Equal("Иванова^Анна", result.File.Dataset.GetString(DicomTag.PatientName));
    }

    [Fact]
    public void ForceWindows1251UsesRawBufferDespiteWrongDeclaredCharset()
    {
        var path = CreateCp1251File(emptyCharset: false, wrongDeclaredCharset: true, patientName: "Петров^Пётр");
        var result = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            EmptyCharsetFallback = EmptyCharsetFallback.Windows1251,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        });
        Assert.Equal("Петров^Пётр", result.File.Dataset.GetString(DicomTag.PatientName));
    }

    [Fact]
    public void IsoIr144ToUtf8AndNestedSequenceArePreserved()
    {
        var path = CreateStandardFile("ISO_IR 144", "Сидоров^Сидор", includeSequence: true);
        var result = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            SourceEncodingMode = SourceEncodingMode.Automatic,
            TargetEncoding = TargetDicomEncoding.IsoIr192
        });
        Assert.Equal("Сидоров^Сидор", result.File.Dataset.GetString(DicomTag.PatientName));
        Assert.Equal("Описание", result.File.Dataset.GetSequence(DicomTag.ReferencedStudySequence).Items[0].GetString(DicomTag.StudyDescription));
    }

    [Fact]
    public void IsoIr144RejectsUnrepresentableCharacter()
    {
        var path = CreateStandardFile("ISO_IR 192", "Иванов^😀", includeSequence: false);
        var ex = Assert.Throws<DicomEncodingException>(() => new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            SourceEncodingMode = SourceEncodingMode.Automatic,
            TargetEncoding = TargetDicomEncoding.IsoIr144
        }));
        Assert.Equal(DicomTag.PatientName, ex.Tag);
    }

    [Fact]
    public void NoChangePreviewClearlyKeepsOriginal()
    {
        var path = CreateStandardFile("ISO_IR 192", "Иванов^Иван", false);
        var preview = new DicomTextTranscoder().Preview(path, new EncodingRule { TargetEncoding = TargetDicomEncoding.NoChange });
        Assert.Contains("оригинал", preview.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedDatasetIsClassifiedPerElementFromRawBytes()
    {
        var path = CreateMixedUtf8File("������ � �^", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));
        var items = new DicomTextElementAnalyzer().Analyze(DicomFile.Open(path, FileReadOption.ReadAll));

        Assert.Equal(DicomTextElementStatus.Irrecoverable, items.Single(x => x.Tag == DicomTag.PatientName.ToString()).Status);
        var protocol = items.Single(x => x.Tag == DicomTag.ProtocolName.ToString());
        Assert.Equal(DicomTextElementStatus.Recoverable, protocol.Status);
        Assert.Equal("Windows-1251", protocol.DetectedEncoding);
        Assert.Equal("<Общий протокол>", protocol.RepairedValue);
        Assert.Equal(DicomTextElementStatus.Ascii, items.Single(x => x.Tag == DicomTag.InstitutionName.ToString()).Status);
    }

    [Fact]
    public void RepairModeBlocksIrrecoverablePatientName()
    {
        var path = CreateMixedUtf8File("������ � �^", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));
        var ex = Assert.Throws<DicomEncodingException>(() => new DicomTextTranscoder().Transcode(path,
            new EncodingRule { ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements }));
        Assert.Equal(DicomTag.PatientName, ex.Tag);
        Assert.Contains("необратимо", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepairModeChangesOnlyRecoverableElementAndPreservesSourceAndPixels()
    {
        var path = CreateMixedUtf8File("шаферов^с.а.", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var result = new DicomTextTranscoder().Transcode(path,
            new EncodingRule { ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements });

        Assert.Equal("ISO_IR 192", result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
        Assert.Equal("шаферов^с.а.", result.File.Dataset.GetString(DicomTag.PatientName));
        Assert.Equal("<Общий протокол>", result.File.Dataset.GetString(DicomTag.ProtocolName));
        Assert.Equal("GMM Calypso Evo", result.File.Dataset.GetString(DicomTag.InstitutionName));
        Assert.Equal([1, 2, 3, 4], result.File.Dataset.GetDicomItem<DicomOtherByte>(DicomTag.PixelData)!.Buffer.Data);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void CorrectUtf8NeedsNoRepair()
    {
        var path = CreateMixedUtf8File("шаферов^с.а.", "Обычный протокол");
        var items = new DicomTextElementAnalyzer().Analyze(DicomFile.Open(path, FileReadOption.ReadAll));
        Assert.DoesNotContain(items, x => x.Status is DicomTextElementStatus.Recoverable or DicomTextElementStatus.Irrecoverable or DicomTextElementStatus.Ambiguous);
    }

    [Fact]
    public void ExistingRuleDefaultsToLegacyMode()
    {
        Assert.Equal(EncodingProcessingMode.Legacy, new EncodingRule().ProcessingMode);
    }

    [Fact]
    public void RepairModeUsesCp1251ForMissingCharset()
    {
        var path = CreateCp1251File(false, false, "Иванова^Анна", removeCharset: true);
        var result = new DicomTextTranscoder().Transcode(path,
            new EncodingRule { ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements });
        Assert.Equal("Иванова^Анна", result.File.Dataset.GetString(DicomTag.PatientName));
        Assert.Equal("ISO_IR 192", result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
    }

    [Fact]
    public void InvalidUtf8WithoutConfidentCandidateIsAmbiguous()
    {
        var path = CreateMixedUtf8File("Иванов^Иван", "AA");
        PatchAscii(path, "AA", [0x81, 0x20]);
        var protocol = new DicomTextElementAnalyzer().Analyze(DicomFile.Open(path, FileReadOption.ReadAll))
            .Single(x => x.Tag == DicomTag.ProtocolName.ToString());
        Assert.Equal(DicomTextElementStatus.Ambiguous, protocol.Status);
        Assert.Throws<DicomEncodingException>(() => new DicomTextTranscoder().Transcode(path,
            new EncodingRule { ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements }));
    }

    [Fact]
    public void RepairModeProcessesRecoverableElementInsideSequence()
    {
        var path = CreateMixedUtf8File("Иванов^Иван", "Обычный протокол", includeRepairSequence: true);
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));
        var result = new DicomTextTranscoder().Transcode(path,
            new EncodingRule { ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements });
        Assert.Equal("<Общий протокол>", result.File.Dataset.GetSequence(DicomTag.ReferencedStudySequence)
            .Items[0].GetString(DicomTag.StudyDescription));
    }

    [Fact]
    public void SelectedPatientNameBlocksUnselectedRecoverableProtocol()
    {
        var path = CreateMixedUtf8File("Иванов^Иван", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));

        var ex = Assert.Throws<DicomEncodingException>(() => new DicomTextTranscoder().Transcode(path,
            new EncodingRule
            {
                ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
                SelectedTextFields = ["00100010"],
                TargetEncoding = TargetDicomEncoding.IsoIr192
            }));

        Assert.Equal(DicomTag.ProtocolName, ex.Tag);
        Assert.Contains("разрешить", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedProtocolIsRepairedAndDatasetCharsetRemainsConsistent()
    {
        var path = CreateMixedUtf8File("Иванов^Иван", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));

        var result = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SelectedTextFields = ["00181030"],
            TargetEncoding = TargetDicomEncoding.IsoIr192
        });

        Assert.Equal("ISO_IR 192", result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
        Assert.Equal("Иванов^Иван", result.File.Dataset.GetString(DicomTag.PatientName));
        Assert.Equal("<Общий протокол>", result.File.Dataset.GetString(DicomTag.ProtocolName));
    }

    [Fact]
    public void RepairCanWriteIsoIr144ButRejectsCyrillicForIsoIr100()
    {
        var path = CreateCp1251File(false, false, "Иванова^Анна", removeCharset: true);
        var selected = new List<string> { "00100010" };

        var result = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SelectedTextFields = selected,
            TargetEncoding = TargetDicomEncoding.IsoIr144
        });
        Assert.Equal("ISO_IR 144", result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
        Assert.Equal("Иванова^Анна", result.File.Dataset.GetString(DicomTag.PatientName));

        var ex = Assert.Throws<DicomEncodingException>(() => new DicomTextTranscoder().Transcode(path,
            new EncodingRule
            {
                ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
                SelectedTextFields = selected,
                TargetEncoding = TargetDicomEncoding.IsoIr100
            }));
        Assert.Equal(DicomTag.PatientName, ex.Tag);
    }

    [Fact]
    public void ExistingRepairRuleWithoutFieldListRepairsAllDetectedFields()
    {
        var path = CreateMixedUtf8File("Иванов^Иван", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));

        var result = new DicomTextTranscoder().Transcode(path,
            new EncodingRule { ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements });

        Assert.Equal("<Общий протокол>", result.File.Dataset.GetString(DicomTag.ProtocolName));
    }

    [Fact]
    public void PatientNameAndProtocolSelectionRepairsBothAndAllFieldsModeIsSupported()
    {
        var path = CreateMixedUtf8File("Иванов^Иван", "1234567890123456");
        PatchAscii(path, "1234567890123456", Encoding.GetEncoding(1251).GetBytes("<Общий протокол>"));

        var selected = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SelectedTextFields = ["00100010", "00181030"]
        });
        var all = new DicomTextTranscoder().Transcode(path, new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SelectedTextFields = [],
            RepairAllTextFields = true
        });

        Assert.Equal("<Общий протокол>", selected.File.Dataset.GetString(DicomTag.ProtocolName));
        Assert.Equal("<Общий протокол>", all.File.Dataset.GetString(DicomTag.ProtocolName));
    }

    [Fact]
    public void PreviewUsesSelectedTargetAndRejectsCyrillicForIsoIr100()
    {
        var path = CreateCp1251File(false, false, "Карнаушенко^Н.Д.", removeCharset: true);
        var preview = new DicomTextTranscoder().Preview(path, new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SelectedTextFields = ["00100010"],
            TargetEncoding = TargetDicomEncoding.IsoIr100
        });

        Assert.Equal("ISO_IR 100", preview.AfterCharset);
        Assert.Contains("не поддерживает", preview.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не будет отправлен", preview.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetDicomEncoding.IsoIr144, "ISO_IR 144")]
    [InlineData(TargetDicomEncoding.IsoIr192, "ISO_IR 192")]
    public void ForcedCp1251UsesSameSerializedResultForPreviewAndProduction(TargetDicomEncoding target, string charset)
    {
        var path = CreateCp1251File(false, false, "Кузнецова^", removeCharset: true);
        var rule = new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
            TargetEncoding = target,
            SelectedTextFields = ["00100010"]
        };

        var preview = new DicomTextTranscoder().Preview(path, rule);
        var production = new DicomTextTranscoder().BuildTransformedDicom(path, rule);
        using var stream = new MemoryStream();
        production.File.Save(stream);
        stream.Position = 0;
        var physical = DicomFile.Open(stream, FileReadOption.ReadAll);

        Assert.Equal(charset, physical.Dataset.GetString(DicomTag.SpecificCharacterSet));
        Assert.Equal("Кузнецова^", physical.Dataset.GetString(DicomTag.PatientName));
        var previewPatient = preview.FinalValues!.Single(x => x.TagId == "00100010");
        Assert.Equal("Кузнецова^", previewPatient.BeforeValue);
        Assert.Equal("Кузнецова^", previewPatient.AfterValue);
        Assert.Contains("Windows-1251", previewPatient.SourceEncoding);
        Assert.DoesNotContain('?', physical.Dataset.GetString(DicomTag.PatientName));
        var expectedBytes = DicomEncoding.GetEncoding(charset).GetBytes("Кузнецова^");
        var physicalBytes = physical.Dataset.GetDicomItem<DicomStringElement>(DicomTag.PatientName)!.Buffer.Data;
        Assert.True(physicalBytes.AsSpan().StartsWith(expectedBytes));
    }

    [Fact]
    public void NewRuleDefaultsToPatientNameOnlyAndAutomaticDetection()
    {
        var rule = EncodingRule.CreateNew("Новое", "folder", "pacs");
        Assert.Equal(EncodingProcessingMode.RepairInvalidTextElements, rule.ProcessingMode);
        Assert.Equal(SourceEncodingMode.Automatic, rule.SourceEncodingMode);
        Assert.Equal(TargetDicomEncoding.IsoIr192, rule.TargetEncoding);
        Assert.False(rule.RepairAllTextFields);
        Assert.Equal(["00100010"], rule.SelectedTextFields);
    }

    [Fact]
    public void ForcedIso88595UsesCommonRepairPipeline()
    {
        var path = CreateStandardFile("ISO_IR 144", "Сидоров^Сидор", false);
        var result = new DicomTextTranscoder().BuildTransformedDicom(path, new EncodingRule
        {
            ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
            SourceEncodingMode = SourceEncodingMode.ForceIso88595,
            TargetEncoding = TargetDicomEncoding.IsoIr192,
            SelectedTextFields = ["00100010"]
        });
        Assert.Equal("Сидоров^Сидор", result.File.Dataset.GetString(DicomTag.PatientName));
        Assert.Equal("ISO_IR 192", result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
    }

    [Fact]
    public void LegacyRulesAreMappedToCommonPipelineWithoutLosingAllFieldsSemantics()
    {
        var settings = ValidSettings();
        settings.EncodingRules =
        [
            new EncodingRule
            {
                ProcessingMode = EncodingProcessingMode.Legacy,
                SourceEncodingMode = SourceEncodingMode.ForceWindows1251,
                TargetEncoding = TargetDicomEncoding.IsoIr144,
                DestinationPacsId = "p1",
                SelectedTextFields = null
            }
        ];

        settings.NormalizeLegacy();
        var rule = Assert.Single(settings.EncodingRules);
        Assert.Equal(EncodingProcessingMode.RepairInvalidTextElements, rule.ProcessingMode);
        Assert.Equal(SourceEncodingMode.ForceWindows1251, rule.SourceEncodingMode);
        Assert.Equal(TargetDicomEncoding.IsoIr144, rule.TargetEncoding);
        Assert.Null(rule.SelectedTextFields);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void DiagnosticDistinguishesAsciiEmptyAndMissingSelectedTag()
    {
        var asciiPath = CreateStandardFile("ISO_IR 192", "Иванов^Иван", false);
        var emptyPath = CreateStandardFile("ISO_IR 192", "Иванов^Иван", false);
        var missingPath = CreateStandardFile("ISO_IR 192", "Иванов^Иван", false);
        var asciiFile = DicomFile.Open(asciiPath, FileReadOption.ReadAll);
        asciiFile.Dataset.AddOrUpdate(DicomTag.StudyDescription, "Control study");
        asciiFile.Save(asciiPath);
        var emptyFile = DicomFile.Open(emptyPath, FileReadOption.ReadAll);
        emptyFile.Dataset.AddOrUpdate(DicomTag.StudyDescription, string.Empty);
        emptyFile.Save(emptyPath);
        var rule = EncodingRule.CreateNew("Диагностика", "folder", "pacs");
        rule.SelectedTextFields = ["00081030"];
        var analyzer = new DicomTextElementAnalyzer();

        Assert.Equal(DicomTextElementStatus.Ascii, analyzer.AnalyzeForRule(DicomFile.Open(asciiPath, FileReadOption.ReadAll), rule)
            .Single(x => x.TagId == "00081030").Status);
        Assert.Equal(DicomTextElementStatus.Empty, analyzer.AnalyzeForRule(DicomFile.Open(emptyPath, FileReadOption.ReadAll), rule)
            .Single(x => x.TagId == "00081030").Status);
        Assert.Equal(DicomTextElementStatus.Missing, analyzer.AnalyzeForRule(DicomFile.Open(missingPath, FileReadOption.ReadAll), rule)
            .Single(x => x.TagId == "00081030").Status);
    }

    [Fact]
    public void PrivateUnknownTextTagIsPreservedWhenAllTextFieldsAreEnabled()
    {
        var path = CreateStandardFile("ISO_IR 144", "Иванов^Иван", false);
        var file = DicomFile.Open(path, FileReadOption.ReadAll);
        var privateTag = new DicomTag(0x0021, 0x1103);
        file.Dataset.AddOrUpdate(new DicomLongString(privateTag, "Норма"));
        file.Save(path);
        var rule = EncodingRule.CreateNew("Все поля", "folder", "pacs");
        rule.RepairAllTextFields = true;
        rule.SelectedTextFields = [];

        var result = new DicomTextTranscoder().BuildTransformedDicom(path, rule);

        Assert.Equal("Норма", result.File.Dataset.GetString(privateTag));
        Assert.Equal("ISO_IR 192", result.File.Dataset.GetString(DicomTag.SpecificCharacterSet));
    }

    [Fact]
    public void MissingOrObsoleteSourceModeMigratesToAutomatic()
    {
        var settings = ValidSettings();
        settings.EncodingRules =
        [
            new EncodingRule { DestinationPacsId = "p1", SourceEncodingMode = SourceEncodingMode.UseFallbackWhenCharsetEmpty },
            new EncodingRule { DestinationPacsId = "p1", SourceFolderId = "f1", SourceEncodingMode = (SourceEncodingMode)999 }
        ];
        settings.NormalizeLegacy();
        Assert.All(settings.EncodingRules, rule => Assert.Equal(SourceEncodingMode.Automatic, rule.SourceEncodingMode));
    }

    private string CreateCp1251File(bool emptyCharset, bool wrongDeclaredCharset, string patientName, bool removeCharset = false)
    {
        var path = CreateStandardFile("CP1251TEST", patientName, false);
        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.ASCII.GetBytes("CP1251TEST");
        var index = Find(bytes, marker);
        Assert.True(index >= 0);
        if (removeCharset)
        {
            const int headerLength = 8;
            var shortened = new byte[bytes.Length - headerLength - marker.Length];
            Array.Copy(bytes, 0, shortened, 0, index - headerLength);
            Array.Copy(bytes, index + marker.Length, shortened, index - headerLength, bytes.Length - index - marker.Length);
            bytes = shortened;
        }
        else
        {
            var replacement = Encoding.ASCII.GetBytes(wrongDeclaredCharset ? "ISO_IR 100" : new string(' ', marker.Length));
            Array.Copy(replacement, 0, bytes, index, replacement.Length);
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CreateStandardFile(string charset, string patientName, bool includeSequence)
    {
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SpecificCharacterSet, charset },
            { DicomTag.PatientName, patientName },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([1, 2, 3, 4]))
        };
        if (includeSequence)
            dataset.Add(new DicomSequence(DicomTag.ReferencedStudySequence,
                new DicomDataset { { DicomTag.StudyDescription, "Описание" } }));
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".dcm");
        new DicomFile(dataset).Save(path);
        return path;
    }

    private string CreateMixedUtf8File(string patientName, string protocolName, bool includeRepairSequence = false)
    {
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SpecificCharacterSet, "ISO_IR 192" },
            { DicomTag.PatientName, patientName },
            { DicomTag.InstitutionName, "GMM Calypso Evo" },
            { DicomTag.ProtocolName, protocolName },
            new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer([1, 2, 3, 4]))
        };
        if (includeRepairSequence)
            dataset.Add(new DicomSequence(DicomTag.ReferencedStudySequence,
                new DicomDataset { { DicomTag.StudyDescription, "1234567890123456" } }));
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".dcm");
        new DicomFile(dataset).Save(path);
        return path;
    }

    private static void PatchAscii(string path, string markerText, byte[] replacement)
    {
        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.ASCII.GetBytes(markerText);
        Assert.Equal(marker.Length, replacement.Length);
        var index = Find(bytes, marker);
        Assert.True(index >= 0);
        replacement.CopyTo(bytes, index);
        File.WriteAllBytes(path, bytes);
    }

    private static int Find(byte[] source, byte[] marker)
    {
        for (var i = 0; i <= source.Length - marker.Length; i++)
            if (source.AsSpan(i, marker.Length).SequenceEqual(marker)) return i;
        return -1;
    }

    private static EncodingRule Rule(string? folder, string pacs) => new()
        { SourceFolderId = folder, DestinationPacsId = pacs, Name = "Тест" };

    private static AppSettings ValidSettings() => new()
    {
        PacsServers = [new PacsSettings { Id = "p1" }],
        WatchFolders =
        [
            new WatchFolderSettings { Id = "f1", Path = @"C:\one", PacsIds = ["p1"] },
            new WatchFolderSettings { Id = "f2", Path = @"D:\two", PacsIds = ["p1"] }
        ]
    };

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
