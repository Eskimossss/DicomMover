using System.Globalization;

namespace DicomMover.Models;

internal readonly record struct StudyDateRange(DateTime? From, DateTime? To)
{
    public bool IsActive => From.HasValue || To.HasValue;

    public static StudyDateRange FromFolder(WatchFolderSettings folder)
    {
        var range = new StudyDateRange(
            Boundary(folder.SendStudiesFromDate, folder.SendStudiesFromTime, false, folder.Name),
            Boundary(folder.SendStudiesToDate, folder.SendStudiesToTime, true, folder.Name));
        if (range.From > range.To)
            throw new InvalidDataException($"Для папки «{folder.Name}»: начало периода позже окончания.");
        return range;
    }

    private static DateTime? Boundary(DateTime? date, string? time, bool upper, string folder)
    {
        // Clearing a date disables that boundary, even if its time is still filled in.
        if (!date.HasValue) return null;
        if (string.IsNullOrWhiteSpace(time))
            return upper ? date.Value.Date.AddDays(1).AddTicks(-1) : date.Value.Date;
        if (!TimeSpan.TryParseExact(time.Trim(), [@"hh\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var parsed) ||
            parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
            throw new InvalidDataException($"Для папки «{folder}»: укажите время в формате ЧЧ:ММ (00:00–23:59).");
        return date.Value.Date + parsed;
    }
}
