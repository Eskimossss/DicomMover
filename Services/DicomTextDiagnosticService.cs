using DicomMover.Models;
using FellowOakDicom;

namespace DicomMover.Services;

public sealed class DicomTextDiagnosticService
{
    private const int MaxFiles = 30;

    public DicomTextDiagnosticResult AnalyzeFolder(WatchFolderSettings folder, EncodingRule? rule = null)
    {
        var files = AnalyzeFolderFiles(folder, rule);
        var applicableFiles = files.Where(x => x.IsApplicable).ToList();
        var elements = applicableFiles.SelectMany(x => x.Elements).ToList();
        var charsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in applicableFiles)
        {
            try
            {
                var dicom = DicomFile.Open(file.FilePath, FileReadOption.SkipLargeTags);
                var charset = DicomTextTranscoder.DisplayCharset(dicom.Dataset);
                charsets[charset] = charsets.GetValueOrDefault(charset) + 1;
            }
            catch (Exception) { }
        }
        var recoverable = elements.Count(x => x.Status == DicomTextElementStatus.Recoverable);
        var irrecoverable = elements.Count(x => x.Status == DicomTextElementStatus.Irrecoverable);
        var ambiguous = elements.Count(x => x.Status == DicomTextElementStatus.Ambiguous);
        var recommendation = recoverable > 0
            ? "Рекомендуется: исправлять некорректные текстовые элементы → UTF-8 (ISO_IR 192). Необратимо повреждённые и неоднозначные поля автоматически не исправляются."
            : irrecoverable > 0 || ambiguous > 0
                ? "Безопасно исправимых элементов не найдено. Требуется проверить повреждённые или неоднозначные поля."
                : "Проблем кодировки не обнаружено; преобразование не требуется.";
        return new(files.Count, applicableFiles.Count,
            elements.Count(x => x.Status == DicomTextElementStatus.Ascii),
            elements.Count(x => x.Status == DicomTextElementStatus.Valid), recoverable, irrecoverable, ambiguous,
            charsets, recommendation, elements, recoverable > 0);
    }

    public IReadOnlyList<DicomFileDiagnosticResult> AnalyzeFolderFiles(WatchFolderSettings folder, EncodingRule? rule = null)
    {
        if (!Directory.Exists(folder.Path)) throw new DirectoryNotFoundException($"Папка не найдена: {folder.Path}");
        var candidates = CandidateFiles(folder).ToList();
        var analyzer = new DicomTextElementAnalyzer();
        var resolver = new EncodingRuleResolver();
        var results = new List<DicomFileDiagnosticResult>();
        foreach (var info in candidates)
        {
            if (results.Count >= MaxFiles) break;
            try
            {
                // Диагностике нужны только текстовые метаданные. Pixel Data намеренно не загружается.
                var file = DicomFile.Open(info.FullName, FileReadOption.SkipLargeTags);
                var metadata = EncodingRuleResolver.ReadMetadata(file.Dataset);
                var application = rule is null ? null : resolver.Evaluate(rule, metadata);
                var elements = rule is not null && application!.Applies
                    ? analyzer.AnalyzeForRule(file, rule, info.Name)
                    : analyzer.Analyze(file, EffectiveSourceMode(null), info.Name);
                results.Add(new(info.FullName, info.Name, elements, application, metadata));
            }
            catch (DicomException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return results;
    }

    public IReadOnlyList<DicomConversionPreview> PreviewFolder(WatchFolderSettings folder, EncodingRule rule, int count = MaxFiles) =>
        PreviewFolderFiles(folder, rule, count).Select(x => x.Preview).ToList();

    public IReadOnlyList<DicomFileConversionResult> PreviewFolderFiles(WatchFolderSettings folder, EncodingRule rule, int count = MaxFiles)
    {
        var transcoder = new DicomTextTranscoder();
        var analyzer = new DicomTextElementAnalyzer();
        var resolver = new EncodingRuleResolver();
        var result = new List<DicomFileConversionResult>();
        foreach (var info in CandidateFiles(folder))
        {
            EncodingRuleApplication? application = null;
            DicomRuleMetadata? metadata = null;
            try
            {
                // Условия проверяются по метаданным без загрузки Pixel Data.
                var metadataFile = DicomFile.Open(info.FullName, FileReadOption.SkipLargeTags);
                metadata = EncodingRuleResolver.ReadMetadata(metadataFile.Dataset);
                application = resolver.Evaluate(rule, metadata);
                if (!application.Applies)
                {
                    var elements = analyzer.Analyze(metadataFile, EffectiveSourceMode(null), info.Name);
                    result.Add(new(info.FullName, new(info.Name, DicomTextTranscoder.DisplayCharset(metadataFile.Dataset),
                        TargetName(rule.TargetEncoding), elements, application.Result), application, metadata));
                }
                else
                {
                    result.Add(new(info.FullName, transcoder.Preview(info.FullName, rule), application, metadata));
                }
            }
            catch (DicomEncodingException ex)
            {
                result.Add(new(info.FullName, new(info.Name, "не определено", TargetName(rule.TargetEncoding), [],
                    $"✕ Преобразование невозможно: {ex.Message}"), application, metadata));
            }
            catch (DicomException) { continue; }
            if (result.Count >= count) break;
        }
        return result;
    }

    private static IEnumerable<FileInfo> CandidateFiles(WatchFolderSettings folder)
    {
        var option = folder.SearchSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(folder.Path, "*", option)
            .Where(path => !IsInsideBad(path, folder.Path))
            .Select(path => new FileInfo(path))
            .Where(info => info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(2))
            .OrderByDescending(info => info.LastWriteTimeUtc).Take(MaxFiles * 4);
    }

    private static bool IsInsideBad(string path, string root) => path.StartsWith(
        Path.Combine(Path.GetFullPath(root), "BAD") + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private static SourceEncodingMode EffectiveSourceMode(SourceEncodingMode? mode) => mode switch
    {
        SourceEncodingMode.ForceWindows1251 => SourceEncodingMode.ForceWindows1251,
        SourceEncodingMode.ForceUtf8 => SourceEncodingMode.ForceUtf8,
        SourceEncodingMode.ForceIso88595 => SourceEncodingMode.ForceIso88595,
        _ => SourceEncodingMode.Automatic
    };

    private static string TargetName(TargetDicomEncoding target) => target switch
    {
        TargetDicomEncoding.IsoIr144 => "ISO_IR 144",
        TargetDicomEncoding.IsoIr100 => "ISO_IR 100",
        _ => "ISO_IR 192"
    };
}
