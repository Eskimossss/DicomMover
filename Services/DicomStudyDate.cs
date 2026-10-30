using System.Globalization;

namespace DicomMover.Services;

internal static class DicomStudyDate
{
    private static readonly string[] Formats = ["yyyyMMdd", "yyyy.MM.dd"];

    public static bool TryParse(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static string? Normalize(string? value) =>
        TryParse(value, out var date) ? Format(date) : null;

    public static string Format(DateTime date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
