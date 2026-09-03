namespace DicomMover.Services;

public sealed class UiLogEntry
{
    public DateTime Timestamp { get; }
    public string Line { get; }
    public string ForegroundColor { get; }

    public UiLogEntry(DateTime timestamp, string line)
    {
        Timestamp = timestamp;
        Line = line;
        ForegroundColor = line.Contains("[ERROR]", StringComparison.Ordinal)
            ? "#B42318"
            : line.Contains("[WARN]", StringComparison.Ordinal)
                ? "#B54708"
                : "#344054";
    }
}

public sealed class UiLogBuffer
{
    public const int DefaultMaxEntries = 2000;
    public const int BatchTrimThreshold = 50; // Пакетное удаление пачками по 50 записей

    private readonly List<UiLogEntry> _entries = new();
    private readonly object _lock = new();

    public int MaxEntries { get; }

    public UiLogBuffer(int maxEntries = DefaultMaxEntries)
    {
        MaxEntries = maxEntries;
    }

    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    public bool Add(string line, out UiLogEntry entry, DateTime? timestamp = null)
    {
        lock (_lock)
        {
            entry = new UiLogEntry(timestamp ?? DateTime.Now, line);
            _entries.Add(entry);

            if (_entries.Count >= MaxEntries + BatchTrimThreshold)
            {
                TrimToMax_Locked();
                return true;
            }
            return false;
        }
    }

    public bool Add(string line, DateTime? timestamp = null)
    {
        return Add(line, out _, timestamp);
    }

    public bool TrimTime(TimeSpan maxAge)
    {
        lock (_lock)
        {
            var cutoff = DateTime.Now - maxAge;
            var removeCount = 0;
            while (removeCount < _entries.Count && _entries[removeCount].Timestamp < cutoff)
            {
                removeCount++;
            }
            if (removeCount > 0)
            {
                _entries.RemoveRange(0, removeCount);
                return true;
            }
            return false;
        }
    }

    public void TrimToMax()
    {
        lock (_lock)
        {
            TrimToMax_Locked();
        }
    }

    private void TrimToMax_Locked()
    {
        if (_entries.Count > MaxEntries)
        {
            var excess = _entries.Count - MaxEntries;
            _entries.RemoveRange(0, excess);
        }
    }

    public IReadOnlyList<UiLogEntry> GetSnapshot(string? filter = null)
    {
        lock (_lock)
        {
            IEnumerable<UiLogEntry> query = _entries;
            if (!string.IsNullOrWhiteSpace(filter) && filter != "Все")
            {
                query = filter switch
                {
                    "Ошибки" => query.Where(e => e.Line.Contains("[ERROR]", StringComparison.Ordinal)),
                    "Предупреждения" => query.Where(e => e.Line.Contains("[WARN]", StringComparison.Ordinal)),
                    "Информация" => query.Where(e => e.Line.Contains("[INFO]", StringComparison.Ordinal)),
                    _ => query
                };
            }
            return query.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
