namespace DicomMover.Services;

public static class RussianPluralization
{
    public static string PluralizeStudies(int count)
    {
        var abs = Math.Abs(count);
        var rem100 = abs % 100;
        var rem10 = abs % 10;

        if (rem100 is >= 11 and <= 14)
            return $"{count} исследований";
        if (rem10 == 1)
            return $"{count} исследование";
        if (rem10 is >= 2 and <= 4)
            return $"{count} исследования";
        return $"{count} исследований";
    }

    public static string FormatStudiesEnqueued(int count)
    {
        return $"В очередь отправки на PACS добавлено {PluralizeStudies(count)}.";
    }
}
