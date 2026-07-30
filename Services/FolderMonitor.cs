using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    public FolderMonitor(
        AppSettings settings,
        Database database,
        AppLogger logger)
    {
        _settings = settings;
        _database = database;
        _logger = logger;
    }

   public async Task RunAsync(CancellationToken cancellationToken)
{
    Directory.CreateDirectory(_settings.WatchFolder);

    _logger.Info(
        $"Начато наблюдение за папкой: {_settings.WatchFolder}");

   while (!cancellationToken.IsCancellationRequested)
{
    try
    {
        Scan();

        while (!cancellationToken.IsCancellationRequested)
        {
            var item = _database.GetNextReady();

            if (item is null)
                break;

            await SendAsync(item, cancellationToken);
        }

        await Task.Delay(
            TimeSpan.FromSeconds(
                Math.Max(1, _settings.ScanIntervalSeconds)),
            cancellationToken);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        _logger.Error(
            $"Ошибка мониторинга папки: {ex}");

        await Task.Delay(
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }
}

    _logger.Info("Наблюдение за папкой завершено.");
}

   private void Scan()
{
    var option = _settings.SearchSubfolders
        ? SearchOption.AllDirectories
        : SearchOption.TopDirectoryOnly;

    var files = Directory
        .EnumerateFiles(
            _settings.WatchFolder,
            "*",
            option)
        .ToArray();

    _logger.Info(
        $"Сканирование папки: найдено файлов — {files.Length}");

    foreach (var filePath in files)
    {
        var info = new FileInfo(filePath);

        if (!IsStable(info))
        {
            _logger.Info(
                $"Файл пока не готов: {info.FullName}");
            continue;
        }

        var key = BuildKey(info);

        if (_database.Contains(key))
        {
            _logger.Info(
                $"Файл уже зарегистрирован: {info.FullName}");
            continue;
        }

        _database.Enqueue(key, info.FullName);

        _logger.Info(
            $"Добавлен в очередь: {info.FullName}");
    }
}

    private async Task SendAsync(
        QueueItem item,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(item.FilePath))
        {
            _database.MarkFailed(
                item.Id,
                "Файл не найден.",
                DateTime.UtcNow.AddSeconds(_settings.RetryFailedAfterSeconds));
            return;
        }

        _database.MarkSending(item.Id);
        _logger.Info($"Отправка: {item.FilePath}");

        try
        {
            var dicomFile = await DicomFile.OpenAsync(item.FilePath);
            var uid = dicomFile.Dataset.GetSingleValueOrDefault(
                DicomTag.SOPInstanceUID,
                string.Empty);

            DicomStatus? responseStatus = null;

            var request = new DicomCStoreRequest(item.FilePath)
            {
                OnResponseReceived = (_, response) =>
                    responseStatus = response.Status
            };

            var client = DicomClientFactory.Create(
                _settings.RemoteHost,
                _settings.RemotePort,
                false,
                _settings.LocalAeTitle,
                _settings.RemoteAeTitle);

            await client.AddRequestAsync(request);
            await client.SendAsync(
                cancellationToken,
                DicomClientCancellationMode.ImmediatelyReleaseAssociation);

            if (responseStatus?.State is DicomState.Success or DicomState.Warning)
            {
                _database.MarkSent(
                    item.Id,
                    uid,
                    responseStatus.ToString());

                _logger.Info(
                    $"Отправлено успешно. SOP Instance UID: {uid}; " +
                    $"статус PACS: {responseStatus}");
            }
            else
            {
                throw new InvalidOperationException(
                    $"PACS вернул статус: {responseStatus}");
            }
        }
        catch (Exception ex)
        {
            var nextAttempt = DateTime.UtcNow.AddSeconds(
                _settings.RetryFailedAfterSeconds);

            _database.MarkFailed(item.Id, ex.Message, nextAttempt);
            _logger.Warn(
                $"Ошибка отправки: {ex.Message}. " +
                $"Следующая попытка через {_settings.RetryFailedAfterSeconds} сек.");
        }
    }

    private bool IsStable(FileInfo info)
    {
        if (!info.Exists || info.Length == 0)
            return false;

        if (DateTime.UtcNow - info.LastWriteTimeUtc <
            TimeSpan.FromSeconds(_settings.FileStableSeconds))
            return false;

        try
        {
            using var stream = new FileStream(
                info.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildKey(FileInfo info)
    {
        var value =
            $"{info.FullName.ToUpperInvariant()}|" +
            $"{info.Length}|" +
            $"{info.LastWriteTimeUtc.Ticks}";

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(value)));
    }
}
