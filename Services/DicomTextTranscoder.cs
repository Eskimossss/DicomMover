using System.Text;
using DicomMover.Models;
using FellowOakDicom;

namespace DicomMover.Services;

public sealed class DicomTextTranscoder
{
    public DicomTranscodeResult Transcode(string sourcePath, EncodingRule rule)
    {
        if (rule.ProcessingMode == EncodingProcessingMode.NoChange)
            throw new InvalidOperationException("Режим «Не изменять DICOM» не создаёт преобразованную копию.");
        return BuildTransformedDicom(sourcePath, rule);
    }

    public DicomConversionPreview Preview(string sourcePath, EncodingRule rule)
    {
        if (rule.ProcessingMode == EncodingProcessingMode.NoChange)
        {
            var original = DicomFile.Open(sourcePath, FileReadOption.SkipLargeTags);
            return new(Path.GetFileName(sourcePath), DisplayCharset(original.Dataset), DisplayCharset(original.Dataset), [],
                "Файл не изменяется; при отправке будет использован оригинал.");
        }
        if (rule.TargetEncoding == TargetDicomEncoding.NoChange)
        {
            var original = DicomFile.Open(sourcePath, FileReadOption.SkipLargeTags);
            return new(Path.GetFileName(sourcePath), DisplayCharset(original.Dataset), DisplayCharset(original.Dataset), [],
                "Файл не изменяется; при отправке будет использован оригинал.");
        }

        // Первый проход строит план только по метаданным. Полный Pixel Data читается ниже
        // лишь тогда, когда преобразование действительно можно выполнить.
        var source = DicomFile.Open(sourcePath, FileReadOption.SkipLargeTags);
        var analyzer = new DicomTextElementAnalyzer();
        var mode = EffectiveSourceMode(rule.SourceEncodingMode);
        var sourceElements = analyzer.Analyze(source, mode, Path.GetFileName(sourcePath));
        var analyses = analyzer.AnalyzeForRule(source, rule, Path.GetFileName(sourcePath));
        var plan = ValidateRepairPlan(analyses, rule);
        var required = analyses.Where(x => x.Status == DicomTextElementStatus.Recoverable && !IsSelected(rule, x.TagId))
            .Select(x => x.TagId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (plan.StartsWith('✕'))
            return new(Path.GetFileName(sourcePath), DisplayCharset(source.Dataset), TargetCharset(rule.TargetEncoding),
                analyses, plan, [], required);

        var result = BuildTransformedDicom(sourcePath, rule);
        try
        {
            var verified = analyzer.Analyze(result.File, SourceEncodingMode.Automatic, Path.GetFileName(sourcePath));
            var finalValues = new List<DicomPreviewValue>();
            foreach (var before in sourceElements)
            {
                if (!IsSelected(rule, before.TagId) || before.Status == DicomTextElementStatus.Empty) continue;
                var after = verified.FirstOrDefault(x => x.TagId == before.TagId && x.Path == before.Path);
                if (after is null) continue;
                var sourceValue = before.Status == DicomTextElementStatus.Recoverable ? before.RepairedValue! : before.DisplayValue;
                finalValues.Add(new(before.Path, before.TagId, before.Tag, before.FieldName,
                    before.DetectedEncoding ?? before.DeclaredCharset,
                    sourceValue, after.DisplayValue, sourceValue));
            }
            return new(Path.GetFileName(sourcePath), DisplayCharset(source.Dataset), DisplayCharset(result.File.Dataset),
                analyses, "✓ Преобразование безопасно. Проверен DICOM-файл после сохранения.", finalValues, []);
        }
        finally
        {
            if (!string.IsNullOrEmpty(result.TempFilePath))
            {
                try { File.Delete(result.TempFilePath); } catch { }
            }
        }
    }

    public DicomTranscodeResult BuildTransformedDicom(string sourcePath, EncodingRule rule)
    {
        var file = DicomFile.Open(sourcePath, FileReadOption.Default);
        var analyzer = new DicomTextElementAnalyzer();
        var sourceMode = EffectiveSourceMode(rule.SourceEncodingMode);
        var located = analyzer.AnalyzeLocated(file.Dataset, Path.GetFileName(sourcePath), sourceMode);
        var sourceCharset = DisplayCharset(file.Dataset);
        var targetCharset = TargetCharset(rule.TargetEncoding);
        var targetEncoding = Strict(DicomEncoding.GetEncoding(targetCharset));
        var blocking = located.FirstOrDefault(x => x.Analysis.Status is DicomTextElementStatus.Ambiguous or DicomTextElementStatus.Irrecoverable);
        if (blocking is not null)
        {
            var message = blocking.Analysis.Status == DicomTextElementStatus.Irrecoverable
                ? $"DICOM содержит необратимо повреждённое поле {blocking.Analysis.FieldName}. Исходные символы отсутствуют в файле. Автоматическое восстановление невозможно."
                : $"Не удалось безопасно определить кодировку текстового элемента {blocking.Analysis.FieldName}.";
            throw new DicomEncodingException(message, blocking.Element.Tag, SafeFragment(blocking.Analysis.DisplayValue),
                failureKind: blocking.Analysis.Status == DicomTextElementStatus.Irrecoverable
                    ? DicomEncodingFailureKind.Irrecoverable : DicomEncodingFailureKind.Ambiguous);
        }

        var unselected = located.FirstOrDefault(x => x.Analysis.Status == DicomTextElementStatus.Recoverable && !IsSelected(rule, x.Analysis.TagId));
        if (unselected is not null)
            throw new DicomEncodingException(
                $"Для отправки файла как {targetCharset} необходимо также разрешить исправление поля {unselected.Analysis.FieldName} {unselected.Analysis.Tag}.",
                unselected.Element.Tag, SafeFragment(unselected.Analysis.RepairedValue));

        foreach (var item in located)
        {
            var value = item.Analysis.Status == DicomTextElementStatus.Recoverable
                ? item.Analysis.RepairedValue!
                : item.Analysis.DisplayValue;
            try { _ = targetEncoding.GetBytes(value); }
            catch (EncoderFallbackException ex)
            {
                throw new DicomEncodingException(
                    $"{targetCharset} не может представить значение поля {item.Analysis.FieldName} без потери символов.",
                    item.Element.Tag, SafeFragment(value), ex);
            }
        }

        // Важно: charset устанавливается ДО перестроения строковых элементов. Иначе fo-dicom
        // может закодировать новые строки прежней кодировкой и физически записать '?'.
        file.Dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, targetCharset);
        var repaired = 0;
        foreach (var item in located)
        {
            if (item.Analysis.Status == DicomTextElementStatus.Recoverable)
            {
                repaired++;
                ReplaceElement(item.Owner, item.Element, item.Analysis.RepairedValue!);
            }
            else if (!sourceCharset.Equals(targetCharset, StringComparison.OrdinalIgnoreCase))
            {
                try { ReplaceElement(item.Owner, item.Element, item.Analysis.DisplayValue); }
                catch (Exception ex)
                {
                    throw new DicomEncodingException($"Не удалось безопасно нормализовать поле {item.Analysis.FieldName} для {targetCharset}.",
                        item.Element.Tag, SafeFragment(item.Analysis.DisplayValue), ex);
                }
            }
        }

        var tempFilePath = TempFileManager.CreateTempFilePath();
        try
        {
            TempFileManager.EnsureSufficientDiskSpace(tempFilePath, new FileInfo(sourcePath).Length);
            file.Save(tempFilePath);

            // Контрольное повторное чтение проверяет возможность открытия, сериализованный текст и charset
            // Используется SkipLargeTags для исключения загрузки Pixel Data в оперативную память
            var verified = DicomFile.Open(tempFilePath, FileReadOption.SkipLargeTags);

            var verifiedCharset = verified.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, string.Empty);
            if (!string.Equals(verifiedCharset, targetCharset, StringComparison.OrdinalIgnoreCase))
                throw new DicomEncodingException($"После записи тег SpecificCharacterSet ({verifiedCharset}) не совпадает с целевым ({targetCharset}).");

            var verification = analyzer.Analyze(verified, SourceEncodingMode.Automatic);
            if (verification.Any(x => x.Status is DicomTextElementStatus.Ambiguous or DicomTextElementStatus.Recoverable))
                throw new DicomEncodingException("После преобразования DICOM остались несогласованные текстовые элементы.");
            if (verification.Count != located.Count)
                throw new DicomEncodingException("Контрольное чтение изменило набор текстовых DICOM-элементов.");
            for (var i = 0; i < located.Count; i++)
            {
                var expected = located[i].Analysis.Status == DicomTextElementStatus.Recoverable
                    ? located[i].Analysis.RepairedValue! : located[i].Analysis.DisplayValue;
                var actual = verification[i].DisplayValue;
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    throw new DicomEncodingException("После физической сериализации значение текстового поля изменилось.",
                        located[i].Element.Tag, SafeFragment(actual));
            }
            ValidateNoReplacementCharacters(verified.Dataset);

            // Проверка наличия и целостности файла на диске без полной загрузки Pixel Data в RAM:
            if (file.Dataset.Contains(DicomTag.PixelData))
            {
                var sourceLength = new FileInfo(sourcePath).Length;
                var tempLength = new FileInfo(tempFilePath).Length;
                if (tempLength < sourceLength * 0.8)
                    throw new DicomEncodingException($"Контрольное чтение: размер файла ({tempLength} байт) меньше ожидаемого ({sourceLength} байт), Pixel Data повреждён или не записан.");
            }

            return new DicomTranscodeResult(file, SourceModeDescription(sourceMode), targetCharset, repaired, tempFilePath);
        }
        catch
        {
            try { File.Delete(tempFilePath); } catch { }
            throw;
        }
    }

    private static string ValidateRepairPlan(IReadOnlyList<DicomTextElementAnalysis> analyses, EncodingRule rule)
    {
        var targetCharset = TargetCharset(rule.TargetEncoding);
        var blocking = analyses.FirstOrDefault(x => x.Status is DicomTextElementStatus.Ambiguous or DicomTextElementStatus.Irrecoverable);
        if (blocking is not null)
            return blocking.Status == DicomTextElementStatus.Irrecoverable
                ? $"✕ Преобразование невозможно: поле {blocking.FieldName} уже необратимо повреждено."
                : $"✕ Преобразование невозможно: кодировку поля {blocking.FieldName} нельзя определить безопасно.";
        var unselected = analyses.FirstOrDefault(x => x.Status == DicomTextElementStatus.Recoverable && !IsSelected(rule, x.TagId));
        if (unselected is not null)
            return $"✕ Для формирования корректного {targetCharset} DICOM необходимо также разрешить исправление поля {unselected.FieldName} {unselected.Tag}.";
        var target = Strict(DicomEncoding.GetEncoding(targetCharset));
        foreach (var item in analyses)
        {
            var value = item.Status == DicomTextElementStatus.Recoverable ? item.RepairedValue! : item.DisplayValue;
            try { _ = target.GetBytes(value); }
            catch (EncoderFallbackException)
            {
                return $"✕ Преобразование невозможно: {targetCharset} не поддерживает символы поля {item.FieldName}. Файл не будет отправлен с потерей символов.";
            }
        }
        return $"✓ Преобразование безопасно. Будет исправлено элементов: {analyses.Count(x => x.Status == DicomTextElementStatus.Recoverable)}. Кодировка при отправке: {targetCharset}.";
    }

    private static bool IsSelected(EncodingRule rule, string tagId) =>
        rule.RepairAllTextFields ||
        rule.SourceEncodingMode is SourceEncodingMode.ForceWindows1251 or SourceEncodingMode.ForceUtf8 or SourceEncodingMode.ForceIso88595 ||
        rule.SelectedTextFields is null ||
        rule.SelectedTextFields.Contains(tagId, StringComparer.OrdinalIgnoreCase);

    private static SourceEncodingMode EffectiveSourceMode(SourceEncodingMode mode) =>
        mode == SourceEncodingMode.UseFallbackWhenCharsetEmpty ? SourceEncodingMode.Automatic : mode;

    private static string SourceModeDescription(SourceEncodingMode mode) => mode switch
    {
        SourceEncodingMode.ForceWindows1251 => "Windows-1251 (задано вручную)",
        SourceEncodingMode.ForceUtf8 => "UTF-8 (задано вручную)",
        SourceEncodingMode.ForceIso88595 => "ISO-8859-5 (задано вручную)",
        _ => "автоматическое определение по raw bytes"
    };

    private static string TargetCharset(TargetDicomEncoding target) => target switch
    {
        TargetDicomEncoding.IsoIr144 => "ISO_IR 144",
        TargetDicomEncoding.IsoIr100 => "ISO_IR 100",
        _ => "ISO_IR 192"
    };

    private static void ReplaceElement(DicomDataset dataset, DicomStringElement element, string decoded)
    {
        var values = element.ValueRepresentation is var vr && (vr == DicomVR.LT || vr == DicomVR.ST || vr == DicomVR.UT || vr == DicomVR.UR)
            ? new[] { decoded }
            : decoded.Split('\\');
        dataset.AddOrUpdate(element.ValueRepresentation, element.Tag, values);
    }

    private static Encoding Strict(Encoding encoding) => Encoding.GetEncoding(
        encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    internal static string DisplayCharset(DicomDataset dataset)
    {
        var value = dataset.TryGetString(DicomTag.SpecificCharacterSet, out var charset) ? charset : string.Empty;
        return string.IsNullOrWhiteSpace(value) ? "<пусто>" : value;
    }

    private static void ValidateNoReplacementCharacters(DicomDataset dataset)
    {
        foreach (var sequence in dataset.Where(x => x is DicomSequence).Cast<DicomSequence>())
            foreach (var item in sequence.Items) ValidateNoReplacementCharacters(item);
        foreach (var element in dataset.Where(x => x is DicomStringElement se && se.ValueRepresentation.IsStringEncoded).Cast<DicomStringElement>())
        {
            var value = element.Get<string>();
            if (value.Contains('\uFFFD'))
                throw new DicomEncodingException("Контрольное чтение обнаружило символ замены Unicode.", element.Tag, SafeFragment(value));
        }
    }

    private static string? SafeFragment(string? value) => value is null || value.Length <= 80 ? value : value[..80] + "…";
}
