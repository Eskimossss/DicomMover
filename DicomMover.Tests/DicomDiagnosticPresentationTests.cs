using DicomMover.Models;
using Xunit;

namespace DicomMover.Tests;

public sealed class DicomDiagnosticPresentationTests
{
    [Fact]
    public void RecoverableElementShowsRecoveredTextAndActualEncoding()
    {
        var row = new DiagnosticElementRow
        {
            Source = Element(DicomTextElementStatus.Recoverable, display: "<невалидно>", repaired: "Кузнецова^Анна", detected: "Windows-1251")
        };

        Assert.Equal("Кузнецова^Анна", row.SourceValue);
        Assert.Equal("Windows-1251", row.SourceEncoding);
        Assert.Equal("Можно исправить", row.Status);
        Assert.True(row.IsProblem);
        Assert.True(row.IsRepairable);
    }

    [Theory]
    [InlineData(DicomTextElementStatus.Empty, "<пусто>", "Пустое значение")]
    [InlineData(DicomTextElementStatus.Missing, "—", "Тег отсутствует")]
    public void EmptyAndMissingValuesRemainDistinct(DicomTextElementStatus status, string value, string caption)
    {
        var row = new DiagnosticElementRow { Source = Element(status) };

        Assert.Equal(value, row.SourceValue);
        Assert.Equal(caption, row.Status);
        Assert.False(row.IsProblem);
        Assert.Equal(status == DicomTextElementStatus.Empty, row.IsEmpty);
    }

    [Fact]
    public void ConversionChangedComparesSemanticValues()
    {
        var source = Element(DicomTextElementStatus.Recoverable, repaired: "Иванов^Иван", detected: "Windows-1251");
        var unchanged = new ConversionElementRow
        {
            Source = source,
            Final = new("Dataset/(0010,0010)", "00100010", "(0010,0010)", "Patient Name", "Windows-1251", "Иванов^Иван", "Иванов^Иван"),
            TargetCharset = "ISO_IR 192",
            FileResult = "✓ Преобразование безопасно"
        };
        var changed = new ConversionElementRow
        {
            Source = source,
            Final = unchanged.Final with { AfterValue = "Иванов^Пётр" },
            TargetCharset = "ISO_IR 192",
            FileResult = "✓ Преобразование безопасно"
        };

        Assert.Equal("Нет", unchanged.Changed);
        Assert.Equal("Да", changed.Changed);
        Assert.True(unchanged.Matches(ConversionChangedFilter.Unchanged));
        Assert.False(unchanged.Matches(ConversionChangedFilter.Changed));
        Assert.True(changed.Matches(ConversionChangedFilter.Changed));
        Assert.False(changed.Matches(ConversionChangedFilter.Unchanged));
    }

    [Fact]
    public void NotApplicableChangeAppearsOnlyInAllFilter()
    {
        var row = new ConversionElementRow
        {
            Source = Element(DicomTextElementStatus.Empty),
            Final = null,
            TargetCharset = "ISO_IR 192",
            FileResult = "✓ Преобразование безопасно"
        };

        Assert.Equal("—", row.Changed);
        Assert.True(row.IsEmpty);
        Assert.True(row.Matches(ConversionChangedFilter.All));
        Assert.False(row.Matches(ConversionChangedFilter.Changed));
        Assert.False(row.Matches(ConversionChangedFilter.Unchanged));
    }

    [Fact]
    public void FileProblemFlagsUseStructuredStatusAndPreviewResult()
    {
        var diagnostic = new DicomFileDiagnosticResult("a", "a", [Element(DicomTextElementStatus.Recoverable)]);
        var successful = new DicomFileConversionResult("a", new("a", "", "ISO_IR 192", [], "✓ Преобразование безопасно"));

        Assert.True(diagnostic.HasProblem);
        Assert.False(successful.HasProblem);
    }

    [Fact]
    public void TechnicalDetailsKeepDeclaredCharsetActualEncodingAndOriginalHexSeparate()
    {
        var source = Element(DicomTextElementStatus.Recoverable, repaired: "Кузнецова^", detected: "Windows-1251") with
        {
            DeclaredCharset = "<отсутствует>",
            RawHex = "CA F3 E7 ED E5 F6 EE E2 E0 5E",
            DetectionMethod = "Автоматически"
        };

        var details = DicomElementTechnicalDetails.From(new DiagnosticElementRow { Source = source });

        Assert.Equal("<отсутствует>", details.SourceCharset);
        Assert.Equal("Windows-1251", details.ActualEncoding);
        Assert.Equal("Автоматически", details.DetectionMethod);
        Assert.Equal("CA F3 E7 ED E5 F6 EE E2 E0 5E", details.RawHex);
    }

    [Fact]
    public void ConversionDetailsContainValueBeforeAndAfterReload()
    {
        var source = Element(DicomTextElementStatus.Recoverable, repaired: "Кузнецова^", detected: "Windows-1251");
        var row = new ConversionElementRow
        {
            Source = source,
            Final = new("Dataset/(0010,0010)", "00100010", "(0010,0010)", "Patient Name", "Windows-1251",
                "Кузнецова^", "Кузнецова^", "Кузнецова^"),
            TargetCharset = "ISO_IR 192",
            FileResult = "✓ Преобразование безопасно. Проверен DICOM-файл после сохранения."
        };

        var details = DicomElementTechnicalDetails.From(row);

        Assert.True(details.HasConversion);
        Assert.Equal("ISO_IR 192 / UTF-8", details.TargetEncoding);
        Assert.Equal("ISO_IR 192", details.SavedCharset);
        Assert.Equal("Кузнецова^", details.ValueAfterTransformation);
        Assert.Equal("Кузнецова^", details.ValueAfterReload);
        Assert.Equal("Нет", details.TextChanged);
        Assert.Equal("Да", details.TextMatches);
        Assert.Equal("✓ Значение представимо в целевой кодировке.", details.ElementVerificationResult);
        Assert.Equal(row.FileResult, details.FileVerificationResult);
    }

    [Fact]
    public void FailedFileKeepsElementResultsIndependent()
    {
        const string fileError = "✕ Преобразование невозможно: ISO_IR 100 не поддерживает символы поля Institution Name.";
        var institution = ConversionRow("Institution Name", "СПБ ГБУЗ", DicomTextElementStatus.Valid, fileError);
        var operators = ConversionRow("Operators' Name", "лаборант", DicomTextElementStatus.Valid, fileError);
        var manufacturer = ConversionRow("Manufacturer", "NIPK ELEKTRON", DicomTextElementStatus.Ascii, fileError);

        Assert.Contains("Institution Name", institution.Result);
        Assert.DoesNotContain("Institution Name", operators.Result);
        Assert.Contains("Operators' Name", operators.Result);
        Assert.Equal("✓ Значение представимо в целевой кодировке.", manufacturer.Result);

        var details = DicomElementTechnicalDetails.From(manufacturer);
        Assert.Equal("✓ Значение представимо в целевой кодировке.", details.ElementVerificationResult);
        Assert.Equal(fileError, details.FileVerificationResult);
    }

    [Fact]
    public void TechnicalDetailsUseReadableVrNameAndAsciiCaption()
    {
        var details = DicomElementTechnicalDetails.From(new DiagnosticElementRow
        {
            Source = Element(DicomTextElementStatus.Ascii, display: "NIPK ELEKTRON")
        });

        Assert.Equal("PN (Person Name)", details.VrDisplay);
        Assert.True(details.IsAsciiContent);
        Assert.Equal("Содержимое элемента:", details.ActualEncodingCaption);
        Assert.Equal("только ASCII", details.ActualEncodingDisplay);
    }

    [Fact]
    public void TechnicalDetailsHidePrivateCreatorAndReasonWhenNotApplicable()
    {
        var correct = Element(DicomTextElementStatus.Valid, display: "Иванов^Иван") with
        {
            Explanation = "—",
            PrivateCreator = null
        };

        var details = DicomElementTechnicalDetails.From(new DiagnosticElementRow { Source = correct });

        Assert.False(details.ShowPrivateCreator);
        Assert.False(details.ShowReason);
    }

    [Fact]
    public void TechnicalDetailsShowPrivateCreatorForPrivateTags()
    {
        var privateElement = Element(DicomTextElementStatus.Valid, display: "Текст") with
        {
            Tag = "(0011,1010)",
            TagId = "00111010",
            PrivateCreator = null
        };

        var details = DicomElementTechnicalDetails.From(new DiagnosticElementRow { Source = privateElement });

        Assert.True(details.ShowPrivateCreator);
        Assert.Equal("—", details.PrivateCreator ?? "—");
    }

    private static DicomTextElementAnalysis Element(
        DicomTextElementStatus status,
        string display = "",
        string? repaired = null,
        string? detected = null) =>
        new("sample", "Dataset/(0010,0010)", "(0010,0010)", "00100010", "Patient Name", "PN", "ISO_IR 144",
            status, display, repaired, detected, "00", "declared", "cp1251", "iso88595", true, "Комментарий");

    private static ConversionElementRow ConversionRow(string name, string value, DicomTextElementStatus status, string fileResult) => new()
    {
        Source = Element(status, display: value) with { FieldName = name },
        Final = null,
        TargetCharset = "ISO_IR 100",
        FileResult = fileResult
    };
}
