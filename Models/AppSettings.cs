using System.Text.Json.Serialization;

namespace DicomMover.Models;

public enum PostSendAction
{
    Keep,
    Delete,
    Archive
}

public enum SourceEncodingMode
{
    Automatic,
    UseFallbackWhenCharsetEmpty,
    ForceWindows1251,
    ForceUtf8,
    ForceIso88595
}

public enum EmptyCharsetFallback
{
    DicomDefault,
    Windows1251
}

public enum TargetDicomEncoding
{
    NoChange,
    IsoIr144,
    IsoIr192,
    IsoIr100
}

public enum EncodingProcessingMode
{
    Legacy = 0,
    NoChange = 1,
    RepairInvalidTextElements = 2
}

public sealed class EncodingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Правило кодировки";
    public bool Enabled { get; set; } = true;
    // Legacy = 0 намеренно: правила из предыдущих версий сохраняют прежнюю семантику.
    public EncodingProcessingMode ProcessingMode { get; set; } = EncodingProcessingMode.Legacy;
    public string? SourceFolderId { get; set; }
    public string DestinationPacsId { get; set; } = string.Empty;
    public SourceEncodingMode SourceEncodingMode { get; set; } = SourceEncodingMode.Automatic;
    public EmptyCharsetFallback EmptyCharsetFallback { get; set; } = EmptyCharsetFallback.DicomDefault;
    public TargetDicomEncoding TargetEncoding { get; set; } = TargetDicomEncoding.IsoIr192;
    // null означает правило repair предыдущей версии: исправлять все безопасно определённые поля.
    public List<string>? SelectedTextFields { get; set; }
    public bool RepairAllTextFields { get; set; }

    public static EncodingRule CreateNew(string name, string? sourceFolderId, string destinationPacsId) => new()
    {
        Name = name,
        SourceFolderId = sourceFolderId,
        DestinationPacsId = destinationPacsId,
        ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
        SourceEncodingMode = SourceEncodingMode.Automatic,
        TargetEncoding = TargetDicomEncoding.IsoIr192,
        SelectedTextFields = ["00100010"],
        RepairAllTextFields = false
    };

    public EncodingRule Copy()
    {
        var copy = (EncodingRule)MemberwiseClone();
        copy.SelectedTextFields = SelectedTextFields is null ? null : [.. SelectedTextFields];
        return copy;
    }
}

public sealed class PacsSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Основной PACS";
    public bool Enabled { get; set; } = true;
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 104;
    public string CalledAeTitle { get; set; } = "PACS";
    public string CallingAeTitle { get; set; } = "DICOMMOVER";
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public int SendTimeoutSeconds { get; set; } = 120;

    public PacsSettings Copy() => (PacsSettings)MemberwiseClone();
}

public sealed class WatchFolderSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Папка";
    public string Path { get; set; } = @"C:\testdicom";
    public bool Enabled { get; set; } = true;
    public bool SearchSubfolders { get; set; } = true;
    public List<string> PacsIds { get; set; } = [];
    public PostSendAction PostSendAction { get; set; } = PostSendAction.Keep;
    public string? ArchiveFolder { get; set; }
    public bool PreserveSubfolders { get; set; } = true;
    public bool DeleteEmptySubfolders { get; set; }
    public DateTime? SendStudiesFromDate { get; set; }

    public WatchFolderSettings Copy() => new()
    {
        Id = Id,
        Name = Name,
        Path = Path,
        Enabled = Enabled,
        SearchSubfolders = SearchSubfolders,
        PacsIds = [.. PacsIds],
        PostSendAction = PostSendAction,
        ArchiveFolder = ArchiveFolder,
        PreserveSubfolders = PreserveSubfolders,
        DeleteEmptySubfolders = DeleteEmptySubfolders,
        SendStudiesFromDate = SendStudiesFromDate
    };
}

public sealed class AppSettings
{
    public List<WatchFolderSettings> WatchFolders { get; set; } = [];
    public List<PacsSettings> PacsServers { get; set; } = [];
    public int ScanIntervalSeconds { get; set; } = 45;
    public int PacsHealthCheckSeconds { get; set; } = 60;
    public int FileStableSeconds { get; set; } = 10;
    public int RetryFailedAfterSeconds { get; set; } = 60;
    public int MaxSendAttempts { get; set; } = 5;
    public int UiRetentionHours { get; set; } = 8;
    public int LogRetentionDays { get; set; } = 30;
    public int DatabaseRetentionDays { get; set; } = 365;
    public int BackupRetentionDays { get; set; } = 30;
    public bool EnableDatabaseIntegrityCheck { get; set; }
    public Dictionary<string, bool> VisibleColumns { get; set; } = [];
    public Dictionary<string, double> ColumnWidths { get; set; } = [];
    public List<EncodingRule> EncodingRules { get; set; } = [];
    public double JournalHeight { get; set; } = 190;
    public bool DetailedLogging { get; set; }
    public bool LogPatientNames { get; set; }
    public bool StartWithWindows { get; set; }
    public bool AutoStartMonitoring { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public string DatabaseFile { get; set; } = @"data\dicommover.db";
    public string LogFile { get; set; } = @"logs\dicommover.log";

    // Старые поля читаются только для автоматической миграции существующего JSON.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WatchFolder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool SearchSubfolders { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LocalAeTitle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RemoteAeTitle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RemoteHost { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int RemotePort { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool DeleteAfterSuccessfulSend { get; set; }

    public void NormalizeLegacy()
    {
        WatchFolders ??= [];
        PacsServers ??= [];
        EncodingRules ??= [];
        foreach (var rule in EncodingRules)
        {
            rule.SelectedTextFields = rule.SelectedTextFields?.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!Enum.IsDefined(rule.SourceEncodingMode) || rule.SourceEncodingMode == SourceEncodingMode.UseFallbackWhenCharsetEmpty)
                rule.SourceEncodingMode = SourceEncodingMode.Automatic;
            if (rule.TargetEncoding == TargetDicomEncoding.NoChange)
            {
                rule.Enabled = false;
                rule.TargetEncoding = TargetDicomEncoding.IsoIr192;
            }
            if (rule.ProcessingMode == EncodingProcessingMode.NoChange)
                rule.Enabled = false;
            else if (rule.ProcessingMode == EncodingProcessingMode.Legacy)
            {
                rule.ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements;
            }
        }
        VisibleColumns ??= [];
        ColumnWidths ??= [];
        JournalHeight = Math.Clamp(double.IsFinite(JournalHeight) ? JournalHeight : 190, 120, 600);
        var hasLegacyPacs = !string.IsNullOrWhiteSpace(LocalAeTitle) ||
                            !string.IsNullOrWhiteSpace(RemoteAeTitle) ||
                            !string.IsNullOrWhiteSpace(RemoteHost) || RemotePort != 0;
        var hasLegacyFolder = !string.IsNullOrWhiteSpace(WatchFolder) ||
                              SearchSubfolders || DeleteAfterSuccessfulSend;

        if (PacsServers.Count == 0 && (hasLegacyPacs || hasLegacyFolder))
        {
            PacsServers.Add(new PacsSettings
            {
                Name = "Основной PACS",
                IpAddress = string.IsNullOrWhiteSpace(RemoteHost) ? "127.0.0.1" : RemoteHost,
                Port = RemotePort is > 0 and <= 65535 ? RemotePort : 104,
                CalledAeTitle = string.IsNullOrWhiteSpace(RemoteAeTitle) ? "PACS" : RemoteAeTitle,
                CallingAeTitle = string.IsNullOrWhiteSpace(LocalAeTitle) ? "DICOMMOVER" : LocalAeTitle
            });
        }

        if (WatchFolders.Count == 0 && hasLegacyFolder)
        {
            WatchFolders.Add(new WatchFolderSettings
            {
                Name = "Основная папка",
                Path = string.IsNullOrWhiteSpace(WatchFolder) ? @"C:\testdicom" : WatchFolder,
                SearchSubfolders = SearchSubfolders,
                PacsIds = [PacsServers[0].Id],
                PostSendAction = DeleteAfterSuccessfulSend ? PostSendAction.Delete : PostSendAction.Keep
            });
        }

        // Ранние тестовые версии поддерживали правило «Любая папка». Теперь каждое правило
        // однозначно относится к источнику; старое правило безопасно привязываем к первой папке.
        if (WatchFolders.FirstOrDefault() is { } firstFolder)
            foreach (var rule in EncodingRules.Where(r => string.IsNullOrWhiteSpace(r.SourceFolderId)))
                rule.SourceFolderId = firstFolder.Id;

        WatchFolder = null;
        LocalAeTitle = null;
        RemoteAeTitle = null;
        RemoteHost = null;
        RemotePort = 0;
        DeleteAfterSuccessfulSend = false;
    }

    public AppSettings Copy() => new()
    {
        WatchFolders = WatchFolders.Select(folder => folder.Copy()).ToList(),
        PacsServers = PacsServers.Select(pacs => pacs.Copy()).ToList(),
        ScanIntervalSeconds = ScanIntervalSeconds,
        PacsHealthCheckSeconds = PacsHealthCheckSeconds,
        FileStableSeconds = FileStableSeconds,
        RetryFailedAfterSeconds = RetryFailedAfterSeconds,
        MaxSendAttempts = MaxSendAttempts,
        UiRetentionHours = UiRetentionHours,
        LogRetentionDays = LogRetentionDays,
        DatabaseRetentionDays = DatabaseRetentionDays,
        BackupRetentionDays = BackupRetentionDays,
        EnableDatabaseIntegrityCheck = EnableDatabaseIntegrityCheck,
        VisibleColumns = new Dictionary<string, bool>(VisibleColumns),
        ColumnWidths = new Dictionary<string, double>(ColumnWidths),
        EncodingRules = EncodingRules.Select(rule => rule.Copy()).ToList(),
        JournalHeight = JournalHeight,
        DetailedLogging = DetailedLogging,
        LogPatientNames = LogPatientNames,
        StartWithWindows = StartWithWindows,
        AutoStartMonitoring = AutoStartMonitoring,
        StartMinimized = StartMinimized,
        MinimizeToTray = MinimizeToTray,
        DatabaseFile = DatabaseFile,
        LogFile = LogFile
    };

    public void Validate()
    {
        NormalizeLegacy();
        if (WatchFolders.Count == 0 || PacsServers.Count == 0)
            throw new InvalidDataException("Нужна хотя бы одна папка и один PACS.");
        if (ScanIntervalSeconds is < 1 or > 86400 || PacsHealthCheckSeconds is < 10 or > 3600 || FileStableSeconds is < 0 or > 86400 ||
            RetryFailedAfterSeconds is < 1 or > 86400 || MaxSendAttempts is < 1 or > 100)
            throw new InvalidDataException("Проверьте интервалы и количество попыток.");
        if (UiRetentionHours is < 1 or > 720 || LogRetentionDays is < 1 or > 3650 ||
            DatabaseRetentionDays is < 30 or > 3650 || BackupRetentionDays is < 1 or > 3650)
            throw new InvalidDataException("Проверьте сроки отображения и хранения журналов.");

        var duplicatePacsId = PacsServers.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicatePacsId is not null) throw new InvalidDataException("Обнаружены повторяющиеся идентификаторы PACS.");
        var enabledPacsIds = PacsServers.Where(p => p.Enabled).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pacs in PacsServers)
        {
            if (string.IsNullOrWhiteSpace(pacs.Name) || string.IsNullOrWhiteSpace(pacs.IpAddress) ||
                pacs.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(pacs.CalledAeTitle) ||
                pacs.CalledAeTitle.Length > 16 || string.IsNullOrWhiteSpace(pacs.CallingAeTitle) ||
                pacs.CallingAeTitle.Length > 16 || pacs.ConnectionTimeoutSeconds is < 1 or > 600 ||
                pacs.SendTimeoutSeconds is < 1 or > 86400)
                throw new InvalidDataException($"Некорректные настройки PACS «{pacs.Name}».");
        }

        foreach (var folder in WatchFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.Name) || string.IsNullOrWhiteSpace(folder.Path))
                throw new InvalidDataException("У каждой папки должны быть название и путь.");
            if (folder.Enabled && !folder.PacsIds.Any(id => enabledPacsIds.Contains(id)))
                throw new InvalidDataException($"Для папки «{folder.Name}» не выбран ни один включённый PACS.");
            if (folder.PostSendAction == PostSendAction.Archive && string.IsNullOrWhiteSpace(folder.ArchiveFolder))
                throw new InvalidDataException($"Для папки «{folder.Name}» не задан архив.");
        }

        foreach (var rule in EncodingRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.SourceFolderId) || string.IsNullOrWhiteSpace(rule.DestinationPacsId))
                throw new InvalidDataException("У каждого правила кодировки должны быть название, исходная папка и PACS назначения.");
            if (!PacsServers.Any(p => string.Equals(p.Id, rule.DestinationPacsId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"В правиле кодировки «{rule.Name}» выбран несуществующий PACS.");
            if (!WatchFolders.Any(f => string.Equals(f.Id, rule.SourceFolderId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"В правиле кодировки «{rule.Name}» выбрана несуществующая папка.");
        }
        var duplicateRule = EncodingRules.Where(r => r.Enabled)
            .GroupBy(r => $"{r.SourceFolderId}|{r.DestinationPacsId}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateRule is not null)
            throw new InvalidDataException("Нельзя создать два активных правила кодировки для одной пары «папка + PACS».");

        var roots = WatchFolders.Where(f => f.Enabled)
            .Select(f => (f.Name, Path: Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar))
            .ToList();
        for (var i = 0; i < roots.Count; i++)
        for (var j = i + 1; j < roots.Count; j++)
        {
            if (roots[i].Path.StartsWith(roots[j].Path, StringComparison.OrdinalIgnoreCase) ||
                roots[j].Path.StartsWith(roots[i].Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Папки «{roots[i].Name}» и «{roots[j].Name}» пересекаются. Оставьте только один корневой путь; поиск в подпапках продолжит работать.");
        }
    }
}
