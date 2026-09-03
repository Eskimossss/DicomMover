using FellowOakDicom;

namespace DicomMover.Models;

public enum DicomTextElementStatus { Ascii, Valid, Recoverable, Irrecoverable, Ambiguous, Empty, Missing }

public sealed record DicomTextElementAnalysis(
    string FileName, string Path, string Tag, string TagId, string FieldName, string Vr, string DeclaredCharset,
    DicomTextElementStatus Status, string DisplayValue, string? RepairedValue, string? DetectedEncoding,
    string RawHex, string DeclaredDecoding, string Windows1251Decoding, string Iso88595Decoding,
    bool IsCritical, string Explanation,
    string DetectionMethod = "Автоматически",
    string? PrivateCreator = null);

public sealed record DicomTextDiagnosticResult(
    int FilesFound,
    int FilesAnalyzed,
    int AsciiElements,
    int ValidElements,
    int RecoverableElements,
    int IrrecoverableElements,
    int AmbiguousElements,
    IReadOnlyDictionary<string, int> Charsets,
    string Recommendation,
    IReadOnlyList<DicomTextElementAnalysis> Elements,
    bool RecommendRepair);

public sealed record DicomConversionPreview(
    string FileName,
    string BeforeCharset,
    string AfterCharset,
    IReadOnlyList<DicomTextElementAnalysis> Elements,
    string Result,
    IReadOnlyList<DicomPreviewValue>? FinalValues = null,
    IReadOnlyList<string>? RequiredFieldTagIds = null);

public sealed record DicomPreviewValue(
    string Path, string TagId, string Tag, string FieldName, string SourceEncoding, string BeforeValue, string AfterValue,
    string? TransformedValue = null);

public sealed record DicomRuleMetadata(string? Modality, string? StationName, string? Manufacturer);

public sealed record EncodingRuleApplication(
    string RuleName, string Conditions, bool Applies, string Result);

public sealed record DicomFileDiagnosticResult(
    string FilePath, string FileName, IReadOnlyList<DicomTextElementAnalysis> Elements,
    EncodingRuleApplication? RuleApplication = null, DicomRuleMetadata? Metadata = null)
{
    public bool IsApplicable => RuleApplication?.Applies != false;
    public bool HasProblem => IsApplicable && Elements.Any(x => x.Status is DicomTextElementStatus.Recoverable or
        DicomTextElementStatus.Irrecoverable or DicomTextElementStatus.Ambiguous);
}

public sealed record DicomFileConversionResult(string FilePath, DicomConversionPreview Preview,
    EncodingRuleApplication? RuleApplication = null, DicomRuleMetadata? Metadata = null)
{
    public string FileName => Preview.FileName;
    public bool IsApplicable => RuleApplication?.Applies != false;
    public bool HasProblem => IsApplicable && !Preview.Result.StartsWith('✓');
}

public sealed record DicomTranscodeResult(DicomFile File, string SourceDescription, string TargetDescription, int RepairedElements = 0, string? TempFilePath = null);

public enum DicomEncodingFailureKind { General, Ambiguous, Irrecoverable }

public sealed class DicomEncodingException : Exception
{
    public DicomEncodingException(string message, DicomTag? tag = null, string? valueFragment = null, Exception? inner = null,
        DicomEncodingFailureKind failureKind = DicomEncodingFailureKind.General)
        : base(message, inner)
    {
        Tag = tag;
        ValueFragment = valueFragment;
        FailureKind = failureKind;
    }

    public DicomTag? Tag { get; }
    public string? ValueFragment { get; }
    public DicomEncodingFailureKind FailureKind { get; }
}
