using System.Globalization;
using DicomMover.Models;

namespace DicomMover.Services;

internal static class DicomStudyDate
{
    private static readonly string[] Formats = ["yyyyMMdd", "yyyy.MM.dd"];

    public static bool TryParse(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static string? Normalize(string? value) =>
        TryParse(value, out var date) ? Format(date) : null;

    public static string Format(DateTime date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static bool IsInRange(string? dateValue, string? timeValue, StudyDateRange range)
    {
        if (!range.IsActive) return true;
        if (!TryParse(dateValue, out var date)) return false;
        if (DateTime.TryParseExact(timeValue?.Trim(),
                ["HH", "HHmm", "HHmmss", "HHmmss.FFFFFF", "HH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var time))
        {
            var timestamp = date + time.TimeOfDay;
            return (!range.From.HasValue || timestamp >= range.From) &&
                   (!range.To.HasValue || timestamp <= range.To);
        }
        // An unknown time is safe only if the whole day is inside the requested range.
        return (!range.From.HasValue || date >= range.From) &&
               (!range.To.HasValue || date.AddDays(1).AddTicks(-1) <= range.To);
    }
}
