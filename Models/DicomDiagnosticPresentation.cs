using System.Text;

namespace DicomMover.Models;

public enum ConversionChangedFilter { All, Changed, Unchanged }

public sealed class DiagnosticElementRow
{
    public required DicomTextElementAnalysis Source { get; init; }
    public string Tag => Source.Tag;
    public string Name => string.IsNullOrWhiteSpace(Source.FieldName) ? "Unknown" : Source.FieldName;
    public string SourceValue => Source.Status switch
    {
        DicomTextElementStatus.Missing => "—",
        DicomTextElementStatus.Empty => "<пусто>",
        DicomTextElementStatus.Recoverable => Source.RepairedValue ?? "—",
        _ => string.IsNullOrEmpty(Source.DisplayValue) ? "<пусто>" : Source.DisplayValue
    };
    public string SourceEncoding => Source.Status switch
    {
        DicomTextElementStatus.Ascii => "ASCII",
        DicomTextElementStatus.Recoverable => Source.DetectedEncoding ?? "Не определена",
        DicomTextElementStatus.Valid => Source.DeclaredCharset,
        DicomTextElementStatus.Empty or DicomTextElementStatus.Missing => "—",
        DicomTextElementStatus.Ambiguous => "Неоднозначно",
        _ => "Не определена"
    };
    public string Status => Source.Status switch
    {
        DicomTextElementStatus.Ascii or DicomTextElementStatus.Valid => "Корректно",
        DicomTextElementStatus.Recoverable => "Можно исправить",
        DicomTextElementStatus.Irrecoverable => "Повреждено",
        DicomTextElementStatus.Ambiguous => "Неоднозначно",
        DicomTextElementStatus.Empty => "Пустое значение",
        _ => "Тег отсутствует"
    };
    public string Comment => Source.Status switch
    {
        DicomTextElementStatus.Ascii or DicomTextElementStatus.Valid => "—",
        _ => Source.Explanation
    };
    public bool IsProblem => Source.Status is DicomTextElementStatus.Recoverable or DicomTextElementStatus.Irrecoverable or DicomTextElementStatus.Ambiguous;
    public bool IsRepairable => Source.Status == DicomTextElementStatus.Recoverable;
    public bool IsEmpty => Source.Status == DicomTextElementStatus.Empty;
}

public sealed class ConversionElementRow
{
    public required DicomTextElementAnalysis Source { get; init; }
    public DicomPreviewValue? Final { get; init; }
    public required string TargetCharset { get; init; }
    public required string FileResult { get; init; }
    public bool SelectedForRepair { get; init; } = true;
    public string Tag => Source.Tag;
    public string Name => string.IsNullOrWhiteSpace(Source.FieldName) ? "Unknown" : Source.FieldName;
    public string SourceValue => new DiagnosticElementRow { Source = Source }.SourceValue;
    public string SourceEncoding => Final?.SourceEncoding ?? new DiagnosticElementRow { Source = Source }.SourceEncoding;
    public string AfterValue => Source.Status switch
    {
        DicomTextElementStatus.Empty => "<пусто>",
        DicomTextElementStatus.Missing => "—",
        _ => Final?.AfterValue ?? "—"
    };
    public string Changed => Final is null ? "—" : string.Equals(Final.BeforeValue, Final.AfterValue, StringComparison.Ordinal) ? "Нет" : "Да";
    public string Result => ElementResult();
    public bool CanEncode => !Result.StartsWith('✕');
    public bool IsProblem => !CanEncode;
    public bool IsEmpty => Source.Status == DicomTextElementStatus.Empty;
    public bool Matches(ConversionChangedFilter filter) => filter switch
    {
        ConversionChangedFilter.Changed => Changed == "Да",
        ConversionChangedFilter.Unchanged => Changed == "Нет",
        _ => true
    };

    private string ElementResult()
    {
        if (Source.Status == DicomTextElementStatus.Empty) return "Пустое значение";
        if (Source.Status == DicomTextElementStatus.Missing) return "Тег отсутствует";
        if (Source.Status == DicomTextElementStatus.Irrecoverable)
            return $"✕ Поле {Name} содержит необратимо повреждённое значение.";
        if (Source.Status == DicomTextElementStatus.Ambiguous)
            return $"✕ Кодировку поля {Name} невозможно определить безопасно.";
        if (Source.Status == DicomTextElementStatus.Recoverable && !SelectedForRepair)
            return $"✕ Для поля {Name} не разрешено исправление исходного текста.";

        try
        {
            _ = StrictTargetEncoding(TargetCharset).GetBytes(SourceValue);
            return "✓ Значение представимо в целевой кодировке.";
        }
        catch (EncoderFallbackException)
        {
            return $"✕ Поле {Name} невозможно безопасно представить в {TargetCharset} без потери символов.";
        }
    }

    private static Encoding StrictTargetEncoding(string charset)
    {
        var codePage = charset switch
        {
            "ISO_IR 100" => 28591,
            "ISO_IR 144" => 28595,
            _ => Encoding.UTF8.CodePage
        };
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

public sealed class DicomElementTechnicalDetails
{
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required string Vr { get; init; }
    public required string SourceValue { get; init; }
    public required string SourceCharset { get; init; }
    public required string ActualEncoding { get; init; }
    public required string DetectionMethod { get; init; }
    public required string RawHex { get; init; }
    public required string Status { get; init; }
    public required string Reason { get; init; }
    public required bool IsPrivateTag { get; init; }
    public string? PrivateCreator { get; init; }
    public string? TargetEncoding { get; init; }
    public string? SavedCharset { get; init; }
    public string? ValueAfterTransformation { get; init; }
    public string? ValueAfterReload { get; init; }
    public string? TextChanged { get; init; }
    public string? ElementVerificationResult { get; init; }
    public string? FileVerificationResult { get; init; }
    public bool HasConversion => TargetEncoding is not null;
    public bool ShowPrivateCreator => IsPrivateTag;
    public bool ShowReason => !string.IsNullOrWhiteSpace(Reason) && Reason != "—";
    public string VrDisplay => VrDescription(Vr);
    public bool IsAsciiContent => ActualEncoding == "ASCII";
    public string ActualEncodingCaption => IsAsciiContent ? "Содержимое элемента:" : "Определённая кодировка:";
    public string ActualEncodingDisplay => IsAsciiContent ? "только ASCII" : ActualEncoding;
    public string TextMatches => TextChanged == "Да" ? "Нет" : TextChanged == "Нет" ? "Да" : "—";

    public static DicomElementTechnicalDetails From(DiagnosticElementRow row) => new()
    {
        Tag = row.Tag,
        Name = row.Name,
        Vr = row.Source.Vr,
        SourceValue = row.SourceValue,
        SourceCharset = row.Source.DeclaredCharset,
        ActualEncoding = row.SourceEncoding,
        DetectionMethod = row.Source.DetectionMethod,
        RawHex = string.IsNullOrWhiteSpace(row.Source.RawHex) ? "—" : row.Source.RawHex,
        Status = row.Status,
        Reason = row.Comment,
        IsPrivateTag = IsPrivateTagId(row.Source.TagId),
        PrivateCreator = row.Source.PrivateCreator
    };

    public static DicomElementTechnicalDetails From(ConversionElementRow row)
    {
        var details = From(new DiagnosticElementRow { Source = row.Source });
        return new()
        {
            Tag = details.Tag,
            Name = details.Name,
            Vr = details.Vr,
            SourceValue = details.SourceValue,
            SourceCharset = details.SourceCharset,
            ActualEncoding = details.ActualEncoding,
            DetectionMethod = details.DetectionMethod,
            RawHex = details.RawHex,
            Status = details.Status,
            Reason = details.Reason,
            IsPrivateTag = details.IsPrivateTag,
            PrivateCreator = details.PrivateCreator,
            TargetEncoding = TargetDescription(row.TargetCharset),
            SavedCharset = row.TargetCharset,
            ValueAfterTransformation = row.Final?.TransformedValue ?? row.Final?.AfterValue ?? "—",
            ValueAfterReload = row.Final?.AfterValue ?? "—",
            TextChanged = row.Changed,
            ElementVerificationResult = row.Result,
            FileVerificationResult = row.FileResult
        };
    }

    public string CopyText =>
        $"Тег: {Tag}\r\nНазвание: {Name}\r\nVR: {VrDisplay}\r\nИсходное значение: {SourceValue}\r\n" +
        $"Specific Character Set файла: {SourceCharset}\r\n{ActualEncodingCaption} {ActualEncodingDisplay}\r\n" +
        $"Способ определения: {DetectionMethod}\r\n" +
        (ShowPrivateCreator ? $"Private Creator: {PrivateCreator ?? "—"}\r\n" : string.Empty) +
        $"Исходные байты: {RawHex}\r\nСтатус: {Status}" +
        (ShowReason ? $"\r\nПричина: {Reason}" : string.Empty) +
        (HasConversion
            ? $"\r\n\r\nПосле преобразования\r\nЦелевая кодировка: {TargetEncoding}\r\n" +
              $"Значение: {ValueAfterTransformation}\r\n\r\n" +
              $"Проверка сохранённого DICOM-файла\r\nSpecific Character Set: {SavedCharset}\r\n" +
              $"Значение после повторного открытия: {ValueAfterReload}\r\nТекст совпадает: {TextMatches}\r\n" +
              $"Результат элемента: {ElementVerificationResult}\r\n\r\n" +
              $"Результат файла\r\n{FileVerificationResult}"
            : string.Empty);

    private static string TargetDescription(string value) => value switch
    {
        "ISO_IR 192" => "ISO_IR 192 / UTF-8",
        "ISO_IR 144" => "ISO_IR 144 / ISO-8859-5",
        "ISO_IR 100" => "ISO_IR 100 / Latin-1",
        _ => value
    };

    private static bool IsPrivateTagId(string value) =>
        value.Length >= 4 && int.TryParse(value[..4], System.Globalization.NumberStyles.HexNumber, null, out var group) && group % 2 == 1;

    private static string VrDescription(string value) => value switch
    {
        "AE" => "AE (Application Entity)",
        "AS" => "AS (Age String)",
        "AT" => "AT (Attribute Tag)",
        "CS" => "CS (Code String)",
        "DA" => "DA (Date)",
        "DS" => "DS (Decimal String)",
        "DT" => "DT (Date Time)",
        "FL" => "FL (Floating Point Single)",
        "FD" => "FD (Floating Point Double)",
        "IS" => "IS (Integer String)",
        "LO" => "LO (Long String)",
        "LT" => "LT (Long Text)",
        "OB" => "OB (Other Byte)",
        "OD" => "OD (Other Double)",
        "OF" => "OF (Other Float)",
        "OL" => "OL (Other Long)",
        "OV" => "OV (Other 64-bit Very Long)",
        "OW" => "OW (Other Word)",
        "PN" => "PN (Person Name)",
        "SH" => "SH (Short String)",
        "SL" => "SL (Signed Long)",
        "SQ" => "SQ (Sequence of Items)",
        "SS" => "SS (Signed Short)",
        "ST" => "ST (Short Text)",
        "SV" => "SV (Signed 64-bit Very Long)",
        "TM" => "TM (Time)",
        "UC" => "UC (Unlimited Characters)",
        "UI" => "UI (Unique Identifier)",
        "UL" => "UL (Unsigned Long)",
        "UN" => "UN (Unknown)",
        "UR" => "UR (Universal Resource Identifier or Universal Resource Locator)",
        "US" => "US (Unsigned Short)",
        "UT" => "UT (Unlimited Text)",
        "UV" => "UV (Unsigned 64-bit Very Long)",
        _ => value
    };
}
