using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using DicomMover.Models;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;

namespace DicomMover.Services;

public sealed class FolderMonitor
{
    private readonly AppSettings _settings;
    private readonly Database _database;
    private readonly AppLogger _logger;
    private readonly HashSet<string> _reportedInaccessible = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PacsHealth> _pacsHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedPacs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dateFilteredFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _deliverySignal = new(0, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pacsCheckLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly EncodingRuleResolver _encodingRuleResolver = new();
    private readonly DicomTextTranscoder _dicomTextTranscoder = new();

    public event Action<string, bool, string?>? PacsAvailabilityChanged;

    public FolderMonitor(AppSettings settings, Database database, AppLogger logger)
    {
        _settings = settings;
        _database = database;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.Info($"Мониторинг запущен. Активных папок: {_settings.WatchFolders.Count(f => f.Enabled)}.");
        try
        {
            await Task.WhenAll(
                RunFolderScanLoopAsync(cancellationToken),
                RunPacsHealthLoopAsync(cancellationToken),
                RunDeliveryLoopAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        _logger.Info("Мониторинг завершён.");
    }

    private async Task RunFolderScanLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var folder in _settings.WatchFolders.Where(f => f.Enabled && !IsFolderPaused(f.Id)))
                    Scan(folder);
                WakeDeliveryLoop();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"Ошибка сканирования папок: {ex}");
            }
            await Task.Delay(TimeSpan.FromSeconds(_settings.ScanIntervalSeconds), cancellationToken);
        }
    }

    private async Task RunPacsHealthLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var pacs in _settings.PacsServers.Where(p => p.Enabled && !IsPacsPaused(p.Id)))
                await CheckPacsHealthAsync(pacs, cancellationToken, force: true);
            await Task.Delay(TimeSpan.FromSeconds(_settings.PacsHealthCheckSeconds), cancellationToken);
        }
    }

    private async Task RunDeliveryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var delivery = _database.GetNextReadyDelivery();
                    if (delivery is null) break;
                    await SendAsync(delivery, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"Ошибка обработки очереди: {ex}");
            }
            await _deliverySignal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public void WakeDeliveryLoop()
    {
        try { if (_deliverySignal.CurrentCount == 0) _deliverySignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Scan(WatchFolderSettings folder)
    {
        var destinations = _settings.PacsServers
            .Where(p => p.Enabled && folder.PacsIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (destinations.Length == 0) return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var checkedFiles = 0;
        var queuedFiles = 0;
        var unstableFiles = 0;
        var knownFiles = 0;
        var invalidFiles = 0;
        var accessErrors = 0;
        var dateFilteredCount = 0;
        var newlyDateFilteredCount = 0;
        foreach (var filePath in EnumerateFilesSafely(folder, () => accessErrors++))
        {
            checkedFiles++;
            var info = new FileInfo(filePath);
            if (!IsStable(info))
            {
                unstableFiles++;
                continue;
            }
            var key = BuildKey(info);
            if (_dateFilteredFiles.Contains(key))
            {
                dateFilteredCount++;
                continue;
            }
            if (_database.Contains(key))
            {
                knownFiles++;
                _database.EnsureDestinationsForExisting(key, destinations);
                continue;
            }
            if (!TryReadDicomMetadata(info.FullName, out var metadata))
            {
                invalidFiles++;
                MoveToBad(folder, info.FullName);
                continue;
            }
            if (folder.SendStudiesFromDate is { } fromDate &&
                (!TryParseDicomDate(metadata.StudyDate, out var studyDate) || studyDate.Date < fromDate.Date))
            {
                _dateFilteredFiles.Add(key);
                dateFilteredCount++;
                newlyDateFilteredCount++;
                continue;
            }
            if (_database.ContainsSopInstanceUid(metadata.SopInstanceUid))
            {
                knownFiles++;
                continue;
            }
            _database.Enqueue(key, info.FullName, metadata.SopInstanceUid, metadata.PatientName, folder.Id, destinations, metadata);
            queuedFiles++;
            _logger.Info(_settings.LogPatientNames && !string.IsNullOrWhiteSpace(metadata.PatientName)
                ? $"Добавлен в очередь: {info.FullName}; пациент: {metadata.PatientName}"
                : $"Добавлен в очередь: {info.FullName}");
        }
        if (newlyDateFilteredCount > 0)
            _logger.Info($"Папка «{folder.Name}»: пропущено по дате Study Date: {newlyDateFilteredCount}. Отправляются исследования с {folder.SendStudiesFromDate:dd.MM.yyyy}.");
        if (_settings.DetailedLogging)
        {
            started.Stop();
            _logger.Info(
                $"Итог сканирования папки «{folder.Name}»: проверено {checkedFiles}, добавлено в очередь {queuedFiles}, " +
                $"ожидают стабильности {unstableFiles}, уже известны {knownFiles}, отфильтровано по дате {dateFilteredCount}, " +
                $"некорректных {invalidFiles}, ошибок доступа {accessErrors}; время {started.Elapsed.TotalSeconds:0.###} сек.");
        }
    }

    private static bool TryParseDicomDate(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private async Task SendAsync(DeliveryItem delivery, CancellationToken cancellationToken)
    {
        var pacs = _settings.PacsServers.FirstOrDefault(p =>
            p.Enabled && string.Equals(p.Id, delivery.PacsId, StringComparison.OrdinalIgnoreCase));
        if (pacs is null)
        {
            _database.MarkDeliveryFailed(delivery, "PACS отключён или удалён.", null, true);
            return;
        }
        if (IsPacsPaused(pacs.Id))
        {
            _database.MarkDeliveryUnavailable(delivery, "PACS временно приостановлен.");
            return;
        }

        var health = await CheckPacsHealthAsync(pacs, cancellationToken);
        if (!health.Available)
        {
            _database.MarkDeliveryUnavailable(delivery, $"PACS недоступен: {health.Error}");
            return;
        }

        _database.MarkDeliverySending(delivery);
        _logger.Info($"Отправка на {pacs.Name}: {delivery.FilePath}");
        DicomStatus? responseStatus = null;
        try
        {
            var encodingRule = _encodingRuleResolver.Resolve(_settings.EncodingRules, delivery.FolderId, delivery.PacsId);
            DicomTranscodeResult? transcodeResult = null;
            var shouldTransform = encodingRule is not null &&
                                  encodingRule.ProcessingMode != EncodingProcessingMode.NoChange &&
                                  encodingRule.TargetEncoding != TargetDicomEncoding.NoChange;
            var request = !shouldTransform
                ? new DicomCStoreRequest(delivery.FilePath)
                : new DicomCStoreRequest((transcodeResult = _dicomTextTranscoder.Transcode(delivery.FilePath, encodingRule!)).File);
            request.OnResponseReceived = (_, response) => responseStatus = response.Status;
            if (transcodeResult is not null)
            {
                _logger.Info($"Применено правило кодировки «{encodingRule!.Name}»: {transcodeResult.SourceDescription} → {transcodeResult.TargetDescription}; файл: {delivery.FilePath}");
                _database.ResolveProblem($"delivery:{delivery.Id}");
            }
            var client = DicomClientFactory.Create(
                pacs.IpAddress, pacs.Port, false, pacs.CallingAeTitle, pacs.CalledAeTitle);
            client.ClientOptions.ConnectionTimeoutInMs = pacs.ConnectionTimeoutSeconds * 1000;
            await client.AddRequestAsync(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(pacs.SendTimeoutSeconds));
            await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);

            if (responseStatus?.State is not (DicomState.Success or DicomState.Warning))
                throw new InvalidOperationException($"PACS вернул статус: {responseStatus}");

            _database.MarkDeliverySent(delivery, responseStatus.ToString());
            _logger.Info($"Отправлено на {pacs.Name}: {delivery.FilePath}; {responseStatus}");
            if (_database.AreAllDeliveriesSent(delivery.QueueItemId))
                ProcessSourceAfterAllDeliveries(delivery);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _database.MarkDeliveryPendingAfterCancellation(delivery);
            throw;
        }
        catch (DicomEncodingException ex)
        {
            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = $"Не удалось выполнить перекодировку без потери данных. PACS: {pacs.Name}; тег: {ex.Tag?.ToString() ?? "не определён"}; " +
                          $"режим: {(_encodingRuleResolver.Resolve(_settings.EncodingRules, delivery.FolderId, delivery.PacsId)?.ProcessingMode.ToString() ?? "не определён")}; " +
                          $"{ex.Message}" + (string.IsNullOrWhiteSpace(ex.ValueFragment) ? "" : $"; фрагмент: {ex.ValueFragment}");
            _database.MarkDeliveryFailed(delivery, details, exhausted ? null : DateTime.UtcNow.Add(retryDelay), exhausted);
            var problemKind = ex.FailureKind switch
            {
                DicomEncodingFailureKind.Irrecoverable => "EncodingIrrecoverable",
                DicomEncodingFailureKind.Ambiguous => "EncodingAmbiguous",
                _ => "Encoding"
            };
            _database.ReportProblem($"delivery:{delivery.Id}", problemKind, Path.GetFileName(delivery.FilePath), details,
                delivery.FilePath, delivery.QueueItemId, delivery.PacsId);
            _logger.Error(details);
        }
        catch (Exception ex)
        {
            var attempts = delivery.AttemptCount + 1;
            if (ex is not DicomFileException && responseStatus is null)
            {
                UpdatePacsHealth(pacs, new PacsHealth(false, DateTime.UtcNow, ex.Message));
                _database.MarkDeliveryUnavailable(delivery, $"PACS или сеть недоступны: {ex.Message}");
                _logger.Warn($"PACS {pacs.Name} недоступен; попытка файла не израсходована: {ex.Message}");
                return;
            }
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            _database.MarkDeliveryFailed(
                delivery,
                ex is OperationCanceledException ? $"Таймаут отправки ({pacs.SendTimeoutSeconds} сек.)" : ex.Message,
                exhausted ? null : DateTime.UtcNow.Add(retryDelay),
                exhausted);
            _logger.Warn($"Ошибка DICOM на {pacs.Name}, попытка {attempts}/{_settings.MaxSendAttempts}; следующий повтор через {retryDelay.TotalMinutes:0} мин.: {ex.Message}");
        }
    }

    private async Task<PacsHealth> CheckPacsHealthAsync(PacsSettings pacs, CancellationToken cancellationToken, bool force = false)
    {
        var checkLock = _pacsCheckLocks.GetOrAdd(pacs.Id, _ => new SemaphoreSlim(1, 1));
        await checkLock.WaitAsync(cancellationToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!force && _pacsHealth.TryGetValue(pacs.Id, out var cached) &&
                DateTime.UtcNow - cached.CheckedAtUtc < TimeSpan.FromSeconds(_settings.PacsHealthCheckSeconds))
                return cached;
            DicomStatus? status = null;
            var request = new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status };
            var client = DicomClientFactory.Create(pacs.IpAddress, pacs.Port, false, pacs.CallingAeTitle, pacs.CalledAeTitle);
            client.ClientOptions.ConnectionTimeoutInMs = pacs.ConnectionTimeoutSeconds * 1000;
            await client.AddRequestAsync(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(pacs.ConnectionTimeoutSeconds));
            await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
            var result = status?.State == DicomState.Success
                ? new PacsHealth(true, DateTime.UtcNow, null)
                : new PacsHealth(false, DateTime.UtcNow, $"C-ECHO: {status}");
            LogDetailedPacsCheck(pacs, result, started.Elapsed);
            UpdatePacsHealth(pacs, result);
            if (!result.Available && !_settings.DetailedLogging)
                _logger.Warn($"PACS {pacs.Name} недоступен: {result.Error}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var result = new PacsHealth(false, DateTime.UtcNow, ex.Message);
            LogDetailedPacsCheck(pacs, result, started.Elapsed);
            UpdatePacsHealth(pacs, result);
            if (!_settings.DetailedLogging)
                _logger.Warn($"PACS {pacs.Name} недоступен: {ex.Message}");
            return result;
        }
        finally { checkLock.Release(); }
    }

    private void LogDetailedPacsCheck(PacsSettings pacs, PacsHealth result, TimeSpan elapsed)
    {
        if (!_settings.DetailedLogging) return;
        var endpoint = $"{pacs.IpAddress}:{pacs.Port}";
        if (result.Available)
            _logger.Info($"Проверка PACS «{pacs.Name}» ({endpoint}): C-ECHO успешен; время {elapsed.TotalSeconds:0.###} сек.");
        else
            _logger.Warn($"Проверка PACS «{pacs.Name}» ({endpoint}): C-ECHO неуспешен — {result.Error}; время {elapsed.TotalSeconds:0.###} сек.");
    }

    private void UpdatePacsHealth(PacsSettings pacs, PacsHealth current)
    {
        _pacsHealth.TryGetValue(pacs.Id, out var previous);
        _pacsHealth[pacs.Id] = current;
        var key = $"pacs:{pacs.Id}";
        if (current.Available)
        {
            _database.ResolveProblem(key);
            _database.ReleaseUnavailableDeliveries(pacs.Id);
            WakeDeliveryLoop();
        }
        else
            _database.ReportProblem(key, "Pacs", pacs.Name, current.Error ?? "PACS недоступен", targetId: pacs.Id);

        if ((previous is null && !current.Available) ||
            (previous is not null && previous.Available != current.Available))
            PacsAvailabilityChanged?.Invoke(pacs.Name, current.Available, current.Error);
    }

    private static TimeSpan GetRetryDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(1)
    };

    private sealed record PacsHealth(bool Available, DateTime CheckedAtUtc, string? Error);

    public void ToggleFolderPause(string folderId)
    {
        lock (_pausedFolders)
        {
            if (!_pausedFolders.Add(folderId)) _pausedFolders.Remove(folderId);
        }
    }

    public void TogglePacsPause(string pacsId)
    {
        lock (_pausedPacs)
        {
            if (!_pausedPacs.Add(pacsId)) _pausedPacs.Remove(pacsId);
        }
    }

    public IReadOnlyList<RuntimeTargetStatus> GetRuntimeStatuses()
    {
        var result = new List<RuntimeTargetStatus>();
        foreach (var folder in _settings.WatchFolders.Where(f => f.Enabled))
            result.Add(new RuntimeTargetStatus("Folder", folder.Id, folder.Name, !IsFolderPaused(folder.Id), null, null));
        foreach (var pacs in _settings.PacsServers.Where(p => p.Enabled))
        {
            _pacsHealth.TryGetValue(pacs.Id, out var health);
            result.Add(new RuntimeTargetStatus(
                "Pacs", pacs.Id, pacs.Name, !IsPacsPaused(pacs.Id),
                health?.Available, health?.CheckedAtUtc));
        }
        return result;
    }

    private bool IsFolderPaused(string id) { lock (_pausedFolders) return _pausedFolders.Contains(id); }
    private bool IsPacsPaused(string id) { lock (_pausedPacs) return _pausedPacs.Contains(id); }

    public sealed record RuntimeTargetStatus(
        string Type, string Id, string Name, bool Active, bool? Available, DateTime? CheckedAtUtc);

    private void ProcessSourceAfterAllDeliveries(DeliveryItem delivery)
    {
        var folder = _settings.WatchFolders.FirstOrDefault(f => f.Id == delivery.FolderId);
        if (folder is null || folder.PostSendAction == PostSendAction.Keep || !IsInside(folder.Path, delivery.FilePath))
            return;
        try
        {
            if (folder.PostSendAction == PostSendAction.Delete)
            {
                File.Delete(delivery.FilePath);
                _logger.Info($"Отправленный файл удалён: {delivery.FilePath}");
            }
            else
            {
                var archiveRoot = Path.GetFullPath(folder.ArchiveFolder!);
                var relative = folder.PreserveSubfolders
                    ? Path.GetRelativePath(Path.GetFullPath(folder.Path), Path.GetFullPath(delivery.FilePath))
                    : Path.GetFileName(delivery.FilePath);
                var destination = Path.Combine(archiveRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(delivery.FilePath, destination, overwrite: false);
                _logger.Info($"Отправленный файл перемещён в архив: {destination}");
            }
            if (folder.DeleteEmptySubfolders) RemoveEmptyParents(Path.GetDirectoryName(delivery.FilePath), folder.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Не удалось обработать отправленный файл {delivery.FilePath}: {ex.Message}");
        }
    }

    private static void RemoveEmptyParents(string? directory, string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory) && IsInside(rootFull, directory) &&
               !string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private IEnumerable<string> EnumerateFilesSafely(WatchFolderSettings folder, Action onAccessError)
    {
        var pending = new Stack<string>();
        pending.Push(folder.Path);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                onAccessError();
                ReportInaccessible(directory, ex); continue;
            }
            _reportedInaccessible.Remove(directory);
            _database.ResolveProblem(FolderProblemKey(directory));
            foreach (var file in files)
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.Temporary)) == 0)
                    yield return file;
            }
            if (!folder.SearchSubfolders) continue;
            try
            {
                foreach (var child in Directory.GetDirectories(directory))
                {
                    if (!string.Equals(Path.GetFileName(child), "BAD", StringComparison.OrdinalIgnoreCase))
                        pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                onAccessError();
                ReportInaccessible(directory, ex);
            }
        }
    }

    private void ReportInaccessible(string directory, Exception ex)
    {
        _database.ReportProblem(FolderProblemKey(directory), "Folder", Path.GetFileName(directory), ex.Message, directory);
        if (_reportedInaccessible.Add(directory)) _logger.Warn($"Нет доступа к папке {directory}: {ex.Message}");
    }

    private static string FolderProblemKey(string directory) => $"folder:{Path.GetFullPath(directory).ToUpperInvariant()}";

    private bool IsStable(FileInfo info)
    {
        if (!info.Exists || info.Length == 0 || DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(_settings.FileStableSeconds))
            return false;
        try { using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read); return stream.Length > 0; }
        catch { return false; }
    }

    private static bool TryReadDicomMetadata(string path, out DicomMetadata metadata)
    {
        metadata = new DicomMetadata();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var file = DicomFile.Open(
                path,
                Encoding.GetEncoding(1251),
                stop: null,
                FileReadOption.SkipLargeTags);
            metadata.SopInstanceUid = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
            var sopClass = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
            var rawName = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty).Split('=', 2)[0];
            metadata.PatientName = string.Join(" ", rawName.Split('^').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            metadata.PatientId = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
            metadata.PatientBirthDate = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty);
            metadata.Modality = file.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
            metadata.StudyInstanceUid = file.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            metadata.AccessionNumber = file.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
            metadata.StudyDate = file.Dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty);
            return !string.IsNullOrWhiteSpace(metadata.SopInstanceUid) && !string.IsNullOrWhiteSpace(sopClass);
        }
        catch { return false; }
    }

    private void MoveToBad(WatchFolderSettings folder, string filePath)
    {
        try
        {
            var badDirectory = Path.Combine(folder.Path, "BAD");
            Directory.CreateDirectory(badDirectory);
            var destination = Path.Combine(badDirectory, Path.GetFileName(filePath));
            if (File.Exists(destination))
                destination = Path.Combine(badDirectory, $"{Path.GetFileNameWithoutExtension(filePath)}-{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(filePath)}");
            File.Move(filePath, destination);
            const string reason = "Файл не распознан как корректный DICOM и перемещён в BAD.";
            _database.RecordRejectedFile(filePath, destination, reason);
            _database.ReportProblem(
                $"bad:{Path.GetFullPath(destination).ToUpperInvariant()}",
                "BadFile",
                Path.GetFileName(destination),
                reason,
                destination,
                targetId: folder.Id);
            _logger.Warn($"Некорректный DICOM перемещён в BAD: {destination}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Не удалось переместить некорректный файл {filePath} в BAD: {ex.Message}");
        }
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string BuildKey(FileInfo info) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
}
