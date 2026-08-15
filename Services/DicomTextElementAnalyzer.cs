using System.Text;
using DicomMover.Models;
using FellowOakDicom;

namespace DicomMover.Services;

public sealed class DicomTextElementAnalyzer
{
    private const int MaxTechnicalBytePreview = 512;
    private const int MaxTechnicalTextPreview = 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Cp1251 = Strict(Encoding.GetEncoding(1251));
    private static readonly Encoding Iso88595 = Strict(Encoding.GetEncoding("iso-8859-5"));

    internal sealed record Located(DicomDataset Owner, DicomStringElement Element, DicomTextElementAnalysis Analysis);

    public IReadOnlyList<DicomTextElementAnalysis> Analyze(DicomFile file, string fileName = "") =>
        AnalyzeLocated(file.Dataset, fileName).Select(x => x.Analysis).ToList();

    public IReadOnlyList<DicomTextElementAnalysis> Analyze(DicomFile file, SourceEncodingMode sourceMode, string fileName = "") =>
        AnalyzeLocated(file.Dataset, fileName, sourceMode).Select(x => x.Analysis).ToList();

    public IReadOnlyList<DicomTextElementAnalysis> AnalyzeForRule(DicomFile file, EncodingRule rule, string fileName = "")
    {
        var result = Analyze(file, EffectiveMode(rule.SourceEncodingMode), fileName).ToList();
        if (rule.RepairAllTextFields || rule.SelectedTextFields is null) return result;
        foreach (var field in KnownFields.Where(x => rule.SelectedTextFields.Contains(x.TagId, StringComparer.OrdinalIgnoreCase)))
        {
            if (result.Any(x => x.TagId.Equals(field.TagId, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new(fileName, field.Tag.ToString(), field.Tag.ToString(), field.TagId, field.Name, "UN",
                "<не применимо>", DicomTextElementStatus.Missing, string.Empty, null, null,
                string.Empty, string.Empty, string.Empty, string.Empty, field.Tag is var tag && (tag == DicomTag.PatientName || tag == DicomTag.PatientID),
                "Тег отсутствует в Dataset."));
        }
        return result;
    }

    private static readonly (string TagId, DicomTag Tag, string Name)[] KnownFields =
    [
        ("00100010", DicomTag.PatientName, "Patient Name"), ("00100020", DicomTag.PatientID, "Patient ID"),
        ("00080080", DicomTag.InstitutionName, "Institution Name"), ("00081030", DicomTag.StudyDescription, "Study Description"),
        ("0008103E", DicomTag.SeriesDescription, "Series Description"), ("00181030", DicomTag.ProtocolName, "Protocol Name"),
        ("00080090", DicomTag.ReferringPhysicianName, "Referring Physician's Name"),
        ("00081050", DicomTag.PerformingPhysicianName, "Performing Physician's Name"),
        ("00081060", new DicomTag(0x0008, 0x1060), "Name of Physician(s) Reading Study"),
        ("00081070", DicomTag.OperatorsName, "Operators' Name")
    ];

    internal IReadOnlyList<Located> AnalyzeLocated(DicomDataset dataset, string fileName = "")
        => AnalyzeLocated(dataset, fileName, SourceEncodingMode.Automatic);

    internal IReadOnlyList<Located> AnalyzeLocated(DicomDataset dataset, string fileName, SourceEncodingMode sourceMode)
    {
        var result = new List<Located>();
        AnalyzeDataset(dataset, fileName, ReadCharset(dataset, null), string.Empty, sourceMode, result);
        return result;
    }

    private static void AnalyzeDataset(DicomDataset dataset, string fileName, string inheritedCharset, string path,
        SourceEncodingMode sourceMode, List<Located> result)
    {
        var charset = ReadCharset(dataset, inheritedCharset);
        foreach (var item in dataset)
        {
            var currentPath = string.IsNullOrEmpty(path) ? item.Tag.ToString() : $"{path}/{item.Tag}";
            if (item is DicomStringElement text && text.ValueRepresentation.IsStringEncoded && text.Tag != DicomTag.SpecificCharacterSet)
                result.Add(new Located(dataset, text, Classify(fileName, currentPath, text, charset, sourceMode)));
            if (item is DicomSequence sequence)
                for (var i = 0; i < sequence.Items.Count; i++)
                    AnalyzeDataset(sequence.Items[i], fileName, charset, $"{currentPath}[{i}]", sourceMode, result);
        }
    }

    private static DicomTextElementAnalysis Classify(string fileName, string path, DicomStringElement element, string charset,
        SourceEncodingMode sourceMode)
    {
        var raw = RemovePadding(element.Buffer.Data, element.ValueRepresentation.PaddingValue);
        var previewLength = Math.Min(raw.Length, MaxTechnicalBytePreview);
        var rawHex = string.Join(" ", Convert.ToHexString(raw.AsSpan(0, previewLength)).Chunk(2).Select(x => new string(x)));
        if (raw.Length > previewLength) rawHex += $" … (показаны первые {previewLength} из {raw.Length} байт)";
        var critical = element.Tag == DicomTag.PatientName || element.Tag == DicomTag.PatientID;
        if (raw.Length == 0)
            return Result(DicomTextElementStatus.Empty, string.Empty, null, null, "Тег присутствует, но не содержит значения.",
                string.Empty, string.Empty, string.Empty);
        if (raw.All(b => b < 0x80))
        {
            var ascii = Encoding.ASCII.GetString(raw);
            return Result(DicomTextElementStatus.Ascii, ascii, null, null, "ASCII-совместимое значение.", ascii, ascii, ascii);
        }

        (Encoding? Encoding, string Name) forcedEncoding = sourceMode switch
        {
            SourceEncodingMode.ForceWindows1251 => (Encoding: Cp1251, Name: "Windows-1251 (задано вручную)"),
            SourceEncodingMode.ForceUtf8 => (Encoding: Utf8, Name: "UTF-8 (задано вручную)"),
            SourceEncodingMode.ForceIso88595 => (Encoding: Iso88595, Name: "ISO-8859-5 (задано вручную)"),
            _ => (null, string.Empty)
        };
        if (forcedEncoding.Encoding is not null)
        {
            var forced = TryDecode(raw, forcedEncoding.Encoding);
            return forced.Valid
                ? Result(DicomTextElementStatus.Recoverable, "<прочитано по ручной настройке>", forced.Value,
                    forcedEncoding.Name, "Исходная кодировка задана вручную.", forced.Text,
                    TryDecode(raw, Cp1251).Text, TryDecode(raw, Iso88595).Text)
                : Result(DicomTextElementStatus.Ambiguous, "<не удалось прочитать в выбранной кодировке>", null,
                    forcedEncoding.Name, "Исходные байты не соответствуют выбранной вручную кодировке.", forced.Text,
                    TryDecode(raw, Cp1251).Text, TryDecode(raw, Iso88595).Text);
        }

        var declared = DecodeDeclared(raw, charset);
        var cp = TryDecode(raw, Cp1251);
        var iso = TryDecode(raw, Iso88595);
        if (declared.Valid)
        {
            if (declared.Value.Contains('\uFFFD'))
                return Result(DicomTextElementStatus.Irrecoverable, declared.Value, null, null,
                    "В исходных байтах уже находятся символы замены U+FFFD. Первоначальные символы отсутствуют.", declared.Text, cp.Text, iso.Text);
            return Result(DicomTextElementStatus.Valid, declared.Value, null, null,
                "Значение соответствует заявленной DICOM-кодировке.", declared.Text, cp.Text, iso.Text);
        }

        var cpScore = cp.Valid ? RussianScore(cp.Value) : int.MinValue;
        var isoScore = iso.Valid ? RussianScore(iso.Value) : int.MinValue;
        if (cpScore >= 12 && cpScore - isoScore >= 6)
            return Result(DicomTextElementStatus.Recoverable, "<невалидно для заявленной кодировки>", cp.Value, "Windows-1251",
                "Элемент не соответствует заявленной кодировке и безопасно распознаётся как Windows-1251.", declared.Text, cp.Text, iso.Text);
        if (isoScore >= 12 && isoScore - cpScore >= 6)
            return Result(DicomTextElementStatus.Recoverable, "<невалидно для заявленной кодировки>", iso.Value, "ISO-8859-5",
                "Элемент не соответствует заявленной кодировке и безопасно распознаётся как ISO-8859-5.", declared.Text, cp.Text, iso.Text);
        return Result(DicomTextElementStatus.Ambiguous, "<не удалось безопасно прочитать>", null, null,
            "Заявленная кодировка не подходит, но надёжно выбрать совместимую кодировку невозможно.", declared.Text, cp.Text, iso.Text);

        DicomTextElementAnalysis Result(DicomTextElementStatus status, string value, string? repaired, string? detected,
            string explanation, string declaredText, string cpText, string isoText) => new(
            fileName, path, element.Tag.ToString(), $"{element.Tag.Group:X4}{element.Tag.Element:X4}", element.Tag.DictionaryEntry.Name, element.ValueRepresentation.Code,
            charset, status, value, repaired, detected, rawHex, TechnicalPreview(declaredText), TechnicalPreview(cpText), TechnicalPreview(isoText), critical, explanation,
            sourceMode is SourceEncodingMode.ForceWindows1251 or SourceEncodingMode.ForceUtf8 or SourceEncodingMode.ForceIso88595
                ? "Задано вручную" : "Автоматически",
            element.Tag.PrivateCreator?.Creator);
    }

    private static string ReadCharset(DicomDataset dataset, string? inherited)
    {
        if (!dataset.Contains(DicomTag.SpecificCharacterSet)) return inherited ?? "<отсутствует>";
        return dataset.TryGetString(DicomTag.SpecificCharacterSet, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : "<пусто>";
    }

    private static (bool Valid, string Value, string Text) DecodeDeclared(byte[] raw, string charset)
    {
        Encoding encoding;
        try
        {
            encoding = charset == "ISO_IR 192" ? Utf8
                : charset is "<отсутствует>" or "<пусто>" ? Strict(Encoding.ASCII)
                : Strict(DicomEncoding.GetEncoding(charset));
        }
        catch (Exception ex) { return (false, string.Empty, $"INVALID: {ex.Message}"); }
        return TryDecode(raw, encoding);
    }

    private static (bool Valid, string Value, string Text) TryDecode(byte[] raw, Encoding encoding)
    {
        try { var value = encoding.GetString(raw); return (true, value, value); }
        catch (DecoderFallbackException ex) { return (false, string.Empty, $"INVALID at byte offset {ex.Index}"); }
    }

    private static int RussianScore(string value)
    {
        var lower = value.ToLowerInvariant();
        var russian = value.Count(c => c is >= 'А' and <= 'я' || c is 'Ё' or 'ё');
        var foreignCyrillic = value.Count(c => c is >= '\u0400' and <= '\u04FF' && !(c is >= 'А' and <= 'я' || c is 'Ё' or 'ё'));
        var controls = value.Count(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');
        var ordinary = value.Count(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || "^.,:;()[]{}<>\"'/-_№".Contains(c));
        var commonBigrams = new[] { "ов", "ан", "на", "ив", "пр", "ро", "то", "ко", "ол", "об", "щи", "ий", "ая", "ый", "ев", "ин", "ер", "ра", "ст", "ен", "ло", "го" };
        var languageBonus = commonBigrams.Sum(pair => CountOccurrences(lower, pair) * 4);
        return russian * 3 + ordinary + languageBonus - foreignCyrillic * 6 - controls * 12 - value.Count(c => c == '\uFFFD') * 20;
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0; index += pattern.Length) count++;
        return count;
    }

    private static byte[] RemovePadding(byte[] raw, byte padding) => raw.Length > 0 && raw[^1] == padding ? raw[..^1] : raw;
    private static string TechnicalPreview(string value) => value.Length <= MaxTechnicalTextPreview
        ? value
        : value[..MaxTechnicalTextPreview] + $"… (показаны первые {MaxTechnicalTextPreview} символов)";
    private static Encoding Strict(Encoding encoding) => Encoding.GetEncoding(
        encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static SourceEncodingMode EffectiveMode(SourceEncodingMode mode) => mode == SourceEncodingMode.UseFallbackWhenCharsetEmpty
        ? SourceEncodingMode.Automatic : mode;
}
