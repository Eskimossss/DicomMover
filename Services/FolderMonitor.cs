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
    internal Func<PacsSettings, DicomCStoreRequest, CancellationToken, Task<DicomStatus?>>? DicomSender { get; set; }
    internal Func<PacsSettings, CancellationToken, Task<bool>>? PacsEchoChecker { get; set; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SuspiciousFileInfo> _suspiciousFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, EncodingRule> _oneTimeEncodingRules = new();

    public void SetOneTimeEncodingRule(long deliveryId, EncodingRule rule) => _oneTimeEncodingRules[deliveryId] = rule;

    public event Action<string, bool, string?>? PacsAvailabilityChanged;

    public FolderMonitor(AppSettings settings, Database database, AppLogger logger)
    {
        _settings = settings;
        _database = database;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TempFileManager.CleanStaleTempFiles();
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

    internal void Scan(WatchFolderSettings folder)
    {
        var destinations = _settings.PacsServers
            .Where(p => p.Enabled && folder.PacsIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(p => (p.IpAddress.Trim().ToUpperInvariant(), p.Port, p.CalledAeTitle.Trim().ToUpperInvariant()))
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
            var inspection = InspectDicomFile(info.FullName);
            var fileProblemKey = $"file:{info.FullName.ToUpperInvariant()}";

            if (inspection.Status is DicomFileInspectionStatus.TemporarilyInaccessible
                                 or DicomFileInspectionStatus.AccessDenied
                                 or DicomFileInspectionStatus.NetworkOrFileError)
            {
                accessErrors++;
                var state = _suspiciousFiles.GetOrAdd(info.FullName, _ => new SuspiciousFileInfo());
                state.TempErrorCount++;
                state.LastError = inspection.ErrorMessage;

                if (state.TempErrorCount >= 3)
                {
                    _database.ReportProblem(
                        fileProblemKey,
                        inspection.Status == DicomFileInspectionStatus.AccessDenied ? "FileAccess" : "FileIO",
                        info.Name,
                        $"Повторяющаяся ошибка доступа к файлу ({state.TempErrorCount} проверок): {inspection.ErrorMessage}",
                        info.FullName,
                        targetId: folder.Id);
                    _logger.Warn($"Файл {info.FullName} временно недоступен ({state.TempErrorCount} проверок): {inspection.ErrorMessage}");
                }
                continue;
            }

            if (inspection.Status == DicomFileInspectionStatus.ConfirmedCorruptDicom)
            {
                invalidFiles++;
                var state = _suspiciousFiles.GetOrAdd(info.FullName, _ => new SuspiciousFileInfo
                {
                    Length = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc
                });

                var isStableAcrossScans = (state.Length == info.Length && state.LastWriteTimeUtc == info.LastWriteTimeUtc);
                if (isStableAcrossScans)
                {
                    state.CorruptScanCycles++;
                }
                else
                {
                    state.Length = info.Length;
                    state.LastWriteTimeUtc = info.LastWriteTimeUtc;
                    state.CorruptScanCycles = 1;
                }
                state.LastError = inspection.ErrorMessage;

                if (isStableAcrossScans && state.CorruptScanCycles >= 3)
                {
                    _suspiciousFiles.TryRemove(info.FullName, out _);
                    MoveToBad(folder, info.FullName, inspection.ErrorMessage ?? "Некорректный формат DICOM.");
                }
                else
                {
                    _logger.Warn($"Подозрение на повреждённый DICOM ({state.CorruptScanCycles}/3 проверок): {info.FullName}; {inspection.ErrorMessage}");
                }
                continue;
            }

            if (_suspiciousFiles.TryRemove(info.FullName, out _))
            {
                _database.ResolveProblem(fileProblemKey);
            }

            var metadata = inspection.Metadata!;
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
        CleanupEmptySubfolders(folder);
    }

    private static bool TryParseDicomDate(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal async Task SendAsync(DeliveryItem delivery, CancellationToken cancellationToken)
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
        EncodingRule? encodingRule = null;
        string? tempFilePath = null;
        try
        {
            if (!File.Exists(delivery.FilePath))
            {
                var errorMsg = $"Исходный файл не найден: {delivery.FilePath}";
                _database.MarkDeliveryFailed(delivery, errorMsg, nextAttempt: null, exhausted: true);
                _database.ReportProblem($"missing:{delivery.Id}", "FileNotFound", Path.GetFileName(delivery.FilePath), errorMsg, delivery.FilePath, delivery.QueueItemId, pacs.Id);
                _logger.Error(errorMsg);
                return;
            }

            var dbOverride = _database.GetDeliveryEncodingOverride(delivery.Id);
            if (dbOverride.HasValue)
            {
                var (srcMode, trgEnc) = dbOverride.Value;
                encodingRule = new EncodingRule
                {
                    Name = $"Разовое решение ({srcMode})",
                    SourceFolderId = delivery.FolderId,
                    DestinationPacsId = delivery.PacsId,
                    ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
                    SourceEncodingMode = srcMode,
                    TargetEncoding = trgEnc,
                    RepairAllTextFields = true
                };
            }
            else if (!_oneTimeEncodingRules.TryRemove(delivery.Id, out encodingRule))
            {
                encodingRule = _encodingRuleResolver.ResolveForFile(
                    _settings.EncodingRules, delivery.FolderId, delivery.PacsId, delivery.FilePath);
            }
            DicomTranscodeResult? transcodeResult = null;
            var shouldTransform = encodingRule is not null &&
                                  encodingRule.ProcessingMode != EncodingProcessingMode.NoChange &&
                                  encodingRule.TargetEncoding != TargetDicomEncoding.NoChange;
            DicomCStoreRequest request;
            if (!shouldTransform)
            {
                request = new DicomCStoreRequest(delivery.FilePath);
            }
            else
            {
                transcodeResult = _dicomTextTranscoder.Transcode(delivery.FilePath, encodingRule!);
                tempFilePath = transcodeResult.TempFilePath;
                request = !string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath)
                    ? new DicomCStoreRequest(tempFilePath)
                    : new DicomCStoreRequest(transcodeResult.File);
            }
            request.OnResponseReceived = (_, response) => responseStatus = response.Status;
            if (transcodeResult is not null)
            {
                _logger.Info($"Применено правило кодировки «{encodingRule!.Name}»: {transcodeResult.SourceDescription} → {transcodeResult.TargetDescription}; файл: {delivery.FilePath}");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(pacs.SendTimeoutSeconds));
            if (DicomSender is not null)
            {
                responseStatus = await DicomSender(pacs, request, timeout.Token);
            }
            else
            {
                var client = DicomClientFactory.Create(
                    pacs.IpAddress, pacs.Port, false, pacs.CallingAeTitle, pacs.CalledAeTitle);
                client.ClientOptions.ConnectionTimeoutInMs = pacs.ConnectionTimeoutSeconds * 1000;
                await client.AddRequestAsync(request);
                await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
            }

            if (responseStatus?.State is not (DicomState.Success or DicomState.Warning))
                throw new InvalidOperationException($"PACS вернул статус: {responseStatus}");

            UpdatePacsHealth(pacs, new PacsHealth(true, DateTime.UtcNow, null));

            _database.MarkDeliverySent(delivery, responseStatus.ToString());
            if (responseStatus.State == DicomState.Warning)
                _logger.Warn($"Отправлено на {pacs.Name} с предупреждением: {delivery.FilePath}; {responseStatus}");
            else
                _logger.Info($"Отправлено на {pacs.Name}: {delivery.FilePath}; {responseStatus}");

            if (_database.AreAllDeliveriesSent(delivery.QueueItemId))
                ProcessSourceAfterAllDeliveries(delivery);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _database.MarkDeliveryPendingAfterCancellation(delivery);
            throw;
        }
        catch (OperationCanceledException)
        {
            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = $"Таймаут отправки ({pacs.SendTimeoutSeconds} сек.)";
            _database.MarkDeliveryFailed(delivery, details, exhausted ? null : DateTime.UtcNow.Add(retryDelay), exhausted);
            if (exhausted)
                _database.ReportProblem($"delivery:{delivery.Id}", "Delivery", $"{Path.GetFileName(delivery.FilePath)} → {pacs.Name}", details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Warn($"Ошибка таймаута DICOM на {pacs.Name}, попытка {attempts}/{_settings.MaxSendAttempts}; следующий повтор через {retryDelay.TotalMinutes:0} мин.");
        }
        catch (FileNotFoundException ex)
        {
            var details = $"Исходный файл не найден: {ex.FileName ?? delivery.FilePath}";
            _database.MarkDeliveryFailed(delivery, details, nextAttempt: null, exhausted: true);
            _database.ReportProblem($"missing:{delivery.Id}", "FileNotFound", Path.GetFileName(delivery.FilePath), details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Error(details);
        }
        catch (DirectoryNotFoundException ex)
        {
            var details = $"Каталог исходного файла не найден: {ex.Message}";
            _database.MarkDeliveryFailed(delivery, details, nextAttempt: null, exhausted: true);
            _database.ReportProblem($"missing:{delivery.Id}", "FileNotFound", Path.GetFileName(delivery.FilePath), details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Error(details);
        }
        catch (InsufficientDiskSpaceException ex)
        {
            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = ex.Message;
            _database.MarkDeliveryFailed(delivery, details, exhausted ? null : DateTime.UtcNow.Add(retryDelay), exhausted);
            _database.ReportProblem($"diskspace:{ex.DriveName}", "DiskSpace", $"Диск {ex.DriveName}", details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Error(details);
        }
        catch (IOException ex) when (!IsNetworkError(ex))
        {
            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = $"Временная ошибка чтения/блокировки файла: {ex.Message}";
            _database.MarkDeliveryFailed(delivery, details, exhausted ? null : DateTime.UtcNow.Add(retryDelay), exhausted);
            if (exhausted)
                _database.ReportProblem($"delivery:{delivery.Id}", "FileAccess", $"{Path.GetFileName(delivery.FilePath)} → {pacs.Name}", details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Warn($"Временная ошибка файла на {pacs.Name}, попытка {attempts}/{_settings.MaxSendAttempts}; повтор через {retryDelay.TotalMinutes:0} мин.: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = $"Ошибка прав доступа к файлу: {ex.Message}";
            _database.MarkDeliveryFailed(delivery, details, exhausted ? null : DateTime.UtcNow.Add(retryDelay), exhausted);
            if (exhausted)
                _database.ReportProblem($"delivery:{delivery.Id}", "FileAccess", $"{Path.GetFileName(delivery.FilePath)} → {pacs.Name}", details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Warn($"Ошибка доступа к файлу {delivery.FilePath}, попытка {attempts}/{_settings.MaxSendAttempts}: {ex.Message}");
        }
        catch (DicomEncodingException ex)
        {
            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = $"Не удалось выполнить перекодировку без потери данных. PACS: {pacs.Name}; тег: {ex.Tag?.ToString() ?? "не определён"}; " +
                          $"режим: {(encodingRule?.ProcessingMode.ToString() ?? "не определён")}; " +
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
        catch (Exception ex) when (IsNetworkError(ex))
        {
            UpdatePacsHealth(pacs, new PacsHealth(false, DateTime.UtcNow, ex.Message));
            _database.MarkDeliveryUnavailable(delivery, $"PACS или сеть недоступны: {ex.Message}");
            _logger.Warn($"PACS {pacs.Name} недоступен (сетевая ошибка); попытка файла не израсходована: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (responseStatus is not null)
                UpdatePacsHealth(pacs, new PacsHealth(true, DateTime.UtcNow, null));

            var attempts = delivery.AttemptCount + 1;
            var exhausted = attempts >= _settings.MaxSendAttempts;
            var retryDelay = GetRetryDelay(attempts);
            var details = responseStatus is not null
                ? $"Отказ PACS C-STORE: {responseStatus}"
                : ex.Message;
            _database.MarkDeliveryFailed(delivery, details, exhausted ? null : DateTime.UtcNow.Add(retryDelay), exhausted);
            if (exhausted)
                _database.ReportProblem($"delivery:{delivery.Id}", "Delivery", $"{Path.GetFileName(delivery.FilePath)} → {pacs.Name}", details, delivery.FilePath, delivery.QueueItemId, pacs.Id);
            _logger.Warn($"Сбой отправки на {pacs.Name}, попытка {attempts}/{_settings.MaxSendAttempts}; следующий повтор через {retryDelay.TotalMinutes:0} мин.: {details}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { }
            }
        }
    }

    private static bool IsNetworkError(Exception ex)
    {
        if (ex is System.Net.Sockets.SocketException or DicomNetworkException or DicomAssociationRejectedException or DicomAssociationAbortedException)
            return true;
        if (ex.InnerException is not null && IsNetworkError(ex.InnerException))
            return true;
        if (ex is AggregateException agg)
            return agg.Flatten().InnerExceptions.Any(IsNetworkError);
        return false;
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(pacs.ConnectionTimeoutSeconds));
            PacsHealth result;
            if (PacsEchoChecker is not null)
            {
                var available = await PacsEchoChecker(pacs, timeout.Token);
                result = available
                    ? new PacsHealth(true, DateTime.UtcNow, null)
                    : new PacsHealth(false, DateTime.UtcNow, "C-ECHO: Unreachable");
            }
            else
            {
                DicomStatus? status = null;
                var request = new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status };
                var client = DicomClientFactory.Create(pacs.IpAddress, pacs.Port, false, pacs.CallingAeTitle, pacs.CalledAeTitle);
                client.ClientOptions.ConnectionTimeoutInMs = pacs.ConnectionTimeoutSeconds * 1000;
                await client.AddRequestAsync(request);
                await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
                result = status?.State == DicomState.Success
                    ? new PacsHealth(true, DateTime.UtcNow, null)
                    : new PacsHealth(false, DateTime.UtcNow, $"C-ECHO: {status}");
            }
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Не удалось обработать отправленный файл {delivery.FilePath}: {ex.Message}");
        }
    }

    internal void CleanupEmptySubfolders(WatchFolderSettings folder)
    {
        if (!folder.DeleteEmptySubfolders || !folder.SearchSubfolders ||
            folder.PostSendAction is not (PostSendAction.Archive or PostSendAction.Delete))
            return;

        try
        {
            if (!Directory.Exists(folder.Path)) return;

            var rootFull = Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var delay = TimeSpan.FromMinutes(folder.EmptyFolderCleanupDelayMinutes);
            var now = DateTime.UtcNow;

            var subdirs = Directory.GetDirectories(rootFull, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length)
                .ToList();

            foreach (var dir in subdirs)
            {
                var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Защита от удаления корня
                if (string.Equals(dirFull, rootFull, StringComparison.OrdinalIgnoreCase) ||
                    !dirFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Пропуск папки BAD
                if (string.Equals(Path.GetFileName(dirFull), "BAD", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var dirInfo = new DirectoryInfo(dirFull);
                    if (!dirInfo.Exists) continue;

                    if (now - dirInfo.LastWriteTimeUtc < delay)
                        continue;

                    if (Directory.EnumerateFileSystemEntries(dirFull).Any())
                        continue;

                    if (_suspiciousFiles.Keys.Any(f => f.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    Directory.Delete(dirFull, recursive: false);
                    _logger.Info($"Удалена пустая подпапка (не изменялась {folder.EmptyFolderCleanupDelayMinutes} мин.): {dirFull}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Игнорируем DirectoryNotEmptyException или временную блокировку
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ошибки обхода пустых папок не должны нарушать работу сканера
        }
    }

    private IEnumerable<string> EnumerateFilesSafely(WatchFolderSettings folder, Action onAccessError) =>
        SafeDirectoryEnumerator.EnumerateFiles(
            folder.Path,
            folder.SearchSubfolders,
            onDirectoryError: (dir, ex) =>
            {
                onAccessError();
                ReportInaccessible(folder, dir, ex);
            },
            onDirectoryResolved: dir =>
            {
                _reportedInaccessible.Remove(dir);
                _database.ResolveProblem(FolderProblemKey(dir));
            });

    private void ReportInaccessible(WatchFolderSettings folder, string directory, Exception ex)
    {
        var isRoot = string.Equals(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        var kind = isRoot ? "FolderRoot" : "FolderSub";
        var title = isRoot ? folder.Name : Path.GetFileName(directory);
        var details = isRoot
            ? $"Корневая отслеживаемая папка недоступна: {ex.Message}"
            : $"Подпапка недоступна: {ex.Message}";

        _database.ReportProblem(FolderProblemKey(directory), kind, title, details, directory, targetId: folder.Id);
        if (_reportedInaccessible.Add(directory))
        {
            _logger.Warn(isRoot
                ? $"Корневая отслеживаемая папка «{folder.Name}» ({directory}) недоступна: {ex.Message}"
                : $"Нет доступа к подпапке {directory}: {ex.Message}");
        }
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
        var result = InspectDicomFile(path);
        metadata = result.Metadata ?? new DicomMetadata();
        return result.Status == DicomFileInspectionStatus.ValidDicom;
    }

    internal static DicomFileInspectionResult InspectDicomFile(string path)
    {
        if (!File.Exists(path))
            return new DicomFileInspectionResult(DicomFileInspectionStatus.NetworkOrFileError, null, $"Файл не существует: {path}");

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var file = DicomFile.Open(
                path,
                Encoding.GetEncoding(1251),
                stop: null,
                FileReadOption.SkipLargeTags);

            var sopInstanceUid = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
            var sopClass = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

            if (string.IsNullOrWhiteSpace(sopInstanceUid) || string.IsNullOrWhiteSpace(sopClass))
            {
                return new DicomFileInspectionResult(
                    DicomFileInspectionStatus.ConfirmedCorruptDicom,
                    null,
                    "Отсутствуют обязательные теги SOPInstanceUID или SOPClassUID.");
            }

            var metadata = new DicomMetadata
            {
                SopInstanceUid = sopInstanceUid
            };
            var rawName = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty).Split('=', 2)[0];
            metadata.PatientName = string.Join(" ", rawName.Split('^').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            metadata.PatientId = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
            metadata.PatientBirthDate = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty);
            metadata.Modality = file.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
            metadata.StudyInstanceUid = file.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            metadata.AccessionNumber = file.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
            metadata.StudyDate = file.Dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty);

            return new DicomFileInspectionResult(DicomFileInspectionStatus.ValidDicom, metadata);
        }
        catch (DicomFileException ex)
        {
            if (ex.InnerException is FileNotFoundException or DirectoryNotFoundException)
                return new DicomFileInspectionResult(DicomFileInspectionStatus.NetworkOrFileError, null, ex.InnerException.Message);
            if (ex.InnerException is UnauthorizedAccessException)
                return new DicomFileInspectionResult(DicomFileInspectionStatus.AccessDenied, null, ex.InnerException.Message);
            if (ex.InnerException is IOException)
                return new DicomFileInspectionResult(DicomFileInspectionStatus.TemporarilyInaccessible, null, ex.InnerException.Message);

            return new DicomFileInspectionResult(DicomFileInspectionStatus.ConfirmedCorruptDicom, null, ex.Message);
        }
        catch (DicomDataException ex)
        {
            if (ex.InnerException is UnauthorizedAccessException)
                return new DicomFileInspectionResult(DicomFileInspectionStatus.AccessDenied, null, ex.InnerException.Message);
            if (ex.InnerException is IOException)
                return new DicomFileInspectionResult(DicomFileInspectionStatus.TemporarilyInaccessible, null, ex.InnerException.Message);

            return new DicomFileInspectionResult(DicomFileInspectionStatus.ConfirmedCorruptDicom, null, ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return new DicomFileInspectionResult(DicomFileInspectionStatus.NetworkOrFileError, null, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return new DicomFileInspectionResult(DicomFileInspectionStatus.NetworkOrFileError, null, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DicomFileInspectionResult(DicomFileInspectionStatus.AccessDenied, null, ex.Message);
        }
        catch (IOException ex)
        {
            return new DicomFileInspectionResult(DicomFileInspectionStatus.TemporarilyInaccessible, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new DicomFileInspectionResult(DicomFileInspectionStatus.ConfirmedCorruptDicom, null, ex.Message);
        }
    }

    private void MoveToBad(WatchFolderSettings folder, string filePath, string? reasonDetails = null)
    {
        try
        {
            var badDirectory = Path.Combine(folder.Path, "BAD");
            Directory.CreateDirectory(badDirectory);
            var destination = Path.Combine(badDirectory, Path.GetFileName(filePath));
            if (File.Exists(destination))
                destination = Path.Combine(badDirectory, $"{Path.GetFileNameWithoutExtension(filePath)}-{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(filePath)}");
            File.Move(filePath, destination);
            var reason = string.IsNullOrWhiteSpace(reasonDetails)
                ? "Файл не распознан как корректный DICOM и перемещён в BAD."
                : $"Файл не распознан как корректный DICOM и перемещён в BAD: {reasonDetails}";
            _database.RecordRejectedFile(filePath, destination, reason);
            _database.ReportProblem(
                $"bad:{Path.GetFullPath(destination).ToUpperInvariant()}",
                "BadFile",
                Path.GetFileName(destination),
                reason,
                destination,
                targetId: folder.Id);
            _logger.Warn($"Некорректный DICOM перемещён из «{filePath}» в BAD: «{destination}»; причина: {reason}");
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

internal sealed class SuspiciousFileInfo
{
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public int CorruptScanCycles { get; set; }
    public int TempErrorCount { get; set; }
    public string? LastError { get; set; }
}

public enum DicomFileInspectionStatus
{
    ValidDicom,
    ConfirmedCorruptDicom,
    TemporarilyInaccessible,
    AccessDenied,
    NetworkOrFileError
}

public sealed record DicomFileInspectionResult(
    DicomFileInspectionStatus Status,
    DicomMetadata? Metadata = null,
    string? ErrorMessage = null);
