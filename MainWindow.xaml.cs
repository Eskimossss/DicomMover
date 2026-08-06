using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.DependencyInjection;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;

namespace DicomMover;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueueRow> _rows = new();
    private readonly Queue<(DateTime Timestamp, string Line)> _visibleLogLines = new();
    private readonly DispatcherTimer _timer;
    private readonly SettingsStore _settingsStore;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private AppSettings _settings = new();
    private AppLogger? _logger;
    private Database? _database;
    private FolderMonitor? _monitor;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _exitRequested;
    private bool _isUiInitialized;
    private DateTime _lastTrayNotificationUtc;

    public MainWindow()
    {
        InitializeComponent();
        _isUiInitialized = true;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        new DicomSetupBuilder().RegisterServices(services => services.AddFellowOakDicom()).Build();
        _settingsStore = new SettingsStore(AppPaths.Resolve("appsettings.json"));

        try
        {
            _settings = _settingsStore.Load();
            if (_settings.WatchFolders.Count > 0 && _settings.PacsServers.Count > 0)
                BuildServices(_settings.Copy());
            ApplyWindowsStartup();
        }
        catch (Exception ex)
        {
            _settings = new AppSettings();
            MessageBox.Show($"Не удалось загрузить настройки или SQLite:\n\n{ex.Message}", "DicomMover", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        QueueGrid.ItemsSource = _rows;
        PopulatePacsFilter();
        ApplyColumnSettings();
        UpdateConfigurationSummary();
        InitializeTray();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => { RefreshQueue(); TrimVisibleLog(); UpdateRuntimeStatusPanel(); };
        _timer.Start();
        Loaded += MainWindow_Loaded;
        System.Windows.Application.Current.SessionEnding += Application_SessionEnding;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && _settings.MinimizeToTray) Hide(); };
        RefreshQueue();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.StartMinimized) { WindowState = WindowState.Minimized; if (_settings.MinimizeToTray) Hide(); }
        if (_settings.AutoStartMonitoring) await StartMonitoringAsync(false);
    }

    private void BuildServices(AppSettings runtimeSettings)
    {
        runtimeSettings.Validate();
        runtimeSettings.DatabaseFile = AppPaths.Resolve(runtimeSettings.DatabaseFile);
        runtimeSettings.LogFile = AppPaths.Resolve(runtimeSettings.LogFile);
        _logger = new AppLogger(runtimeSettings.LogFile);
        _logger.CleanupOldLogs(runtimeSettings.LogRetentionDays);
        _logger.MessageWritten += line => Dispatcher.BeginInvoke(() => AppendVisibleLog(line));
        _database = new Database(runtimeSettings.DatabaseFile);
        _database.ApplyStudyDateFilters(runtimeSettings.WatchFolders);
        MaintainDatabase(runtimeSettings);
        _monitor = new FolderMonitor(runtimeSettings, _database, _logger);
    }

    private void MaintainDatabase(AppSettings runtimeSettings)
    {
        if (_database is null) return;
        var backupDirectory = AppPaths.Resolve("backups");
        Directory.CreateDirectory(backupDirectory);
        var todayBackup = Path.Combine(backupDirectory, $"dicommover-{DateTime.Now:yyyy-MM-dd}.db");
        if (!File.Exists(todayBackup)) _database.BackupTo(todayBackup);
        foreach (var file in Directory.EnumerateFiles(backupDirectory, "dicommover-*.db"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.Date.AddDays(-runtimeSettings.BackupRetentionDays))
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var removed = _database.CleanupOldRecords(runtimeSettings.DatabaseRetentionDays);
        if (removed > 0) _logger?.Info($"Удалено старых подробных записей SQLite: {removed}.");
        if (runtimeSettings.EnableDatabaseIntegrityCheck)
        {
            var marker = Path.Combine(Path.GetDirectoryName(runtimeSettings.DatabaseFile)!, ".last-integrity-check");
            if (!File.Exists(marker) || File.GetLastWriteTimeUtc(marker) < DateTime.UtcNow.AddDays(-7))
            {
                _database.CheckIntegrityAndOptimize();
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
                _logger?.Info("Еженедельная проверка SQLite завершена: целостность в норме.");
            }
        }
    }

    private async Task StartMonitoringAsync(bool showConfigurationReport = true)
    {
        if (_monitorTask is not null) return;
        try
        {
            _settings.Validate();
            if (showConfigurationReport)
            {
                StartButton.IsEnabled = false;
                if (!await ShowConfigurationReportAsync())
                {
                    StartButton.IsEnabled = true;
                    return;
                }
            }
            BuildServices(_settings.Copy());
            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => _monitor!.RunAsync(_cts.Token));
            StartButton.IsEnabled = true;
            StartButton.Content = "■  Остановить мониторинг";
            StartButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38));
            StartButton.BorderBrush = StartButton.Background;
            StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74));
            StatusText.Text = "Мониторинг активен";
            StatusBadge.SetResourceReference(Border.BackgroundProperty, "StatusActiveBackgroundBrush");
            await Task.Delay(50);
        }
        catch (Exception ex)
        {
            _monitorTask = null;
            StartButton.IsEnabled = true;
            StartButton.Content = "▶  Запустить мониторинг";
            StartButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            StartButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "PrimaryBrush");
            MessageBox.Show(ex.Message, "Не удалось запустить мониторинг", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> ShowConfigurationReportAsync()
    {
        var lines = new List<string>();
        var hasErrors = false;
        foreach (var folder in _settings.WatchFolders.Where(f => f.Enabled))
        {
            try
            {
                if (!Directory.Exists(folder.Path)) throw new DirectoryNotFoundException("папка не найдена");
                _ = Directory.EnumerateFileSystemEntries(folder.Path).Take(1).ToList();
                lines.Add($"✓ Папка «{folder.Name}» доступна");
            }
            catch (Exception ex) { hasErrors = true; lines.Add($"✗ Папка «{folder.Name}»: {ex.Message}"); }
        }
        foreach (var pacs in _settings.PacsServers.Where(p => p.Enabled))
        {
            var check = await CheckPacsBeforeStartAsync(pacs);
            if (check.Available)
                lines.Add($"✓ PACS «{pacs.Name}» ({pacs.IpAddress}:{pacs.Port}): C-ECHO успешен");
            else
            {
                hasErrors = true;
                lines.Add($"✗ PACS «{pacs.Name}» ({pacs.IpAddress}:{pacs.Port}): {check.Error}");
            }
        }
        try
        {
            var dbDirectory = Path.GetDirectoryName(AppPaths.Resolve(_settings.DatabaseFile))!;
            Directory.CreateDirectory(dbDirectory);
            lines.Add("✓ Каталог SQLite доступен");
        }
        catch (Exception ex) { hasErrors = true; lines.Add($"✗ SQLite: {ex.Message}"); }

        var text = string.Join(Environment.NewLine, lines);
        if (!hasErrors)
        {
            MessageBox.Show(text, "Проверка конфигурации", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        return MessageBox.Show(text + "\n\nЗапустить мониторинг, несмотря на ошибки?", "Проверка конфигурации", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static async Task<(bool Available, string? Error)> CheckPacsBeforeStartAsync(PacsSettings pacs)
    {
        try
        {
            DicomStatus? responseStatus = null;
            var request = new DicomCEchoRequest
            {
                OnResponseReceived = (_, response) => responseStatus = response.Status
            };
            var client = DicomClientFactory.Create(
                pacs.IpAddress, pacs.Port, false, pacs.CallingAeTitle, pacs.CalledAeTitle);
            client.ClientOptions.ConnectionTimeoutInMs = pacs.ConnectionTimeoutSeconds * 1000;
            await client.AddRequestAsync(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(pacs.ConnectionTimeoutSeconds));
            await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
            return responseStatus?.State == DicomState.Success
                ? (true, null)
                : (false, $"C-ECHO вернул {responseStatus?.ToString() ?? "пустой ответ"}");
        }
        catch (OperationCanceledException)
        {
            return (false, $"таймаут C-ECHO ({pacs.ConnectionTimeoutSeconds} сек.)");
        }
        catch (Exception ex)
        {
            var message = ex is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? ex.Message
                : ex.Message;
            return (false, message);
        }
    }

    private async Task StopMonitoringAsync()
    {
        if (_monitorTask is null) return;
        _cts?.Cancel();
        try { await _monitorTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.Error($"Ошибка остановки мониторинга: {ex}");
            MessageBox.Show(ex.Message, "Ошибка остановки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _monitorTask = null;
            _cts?.Dispose();
            _cts = null;
            StartButton.IsEnabled = true;
            StartButton.Content = "▶  Запустить мониторинг";
            StartButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            StartButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "PrimaryBrush");
            StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(138, 148, 163));
            StatusText.Text = "Мониторинг остановлен";
            StatusBadge.SetResourceReference(Border.BackgroundProperty, "StatusStoppedBackgroundBrush");
        }
    }

    private async void MonitorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        if (_monitorTask is null) await StartMonitoringAsync();
        else await StopMonitoringAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() != true) return;
        var wasRunning = _monitorTask is not null;
        if (wasRunning)
        {
            var answer = MessageBox.Show("Для применения настроек мониторинг будет перезапущен. Продолжить?", "Настройки", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            await StopMonitoringAsync();
        }
        try
        {
            _settingsStore.Save(window.Result);
            _settings = window.Result.Copy();
            ApplyWindowsStartup();
            BuildServices(_settings.Copy());
            UpdateConfigurationSummary();
            PopulatePacsFilter();
            ApplyColumnSettings();
            _logger?.Info("Настройки сохранены.");
            if (wasRunning) await StartMonitoringAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось сохранить настройки", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow(_settings) { Owner = this }.ShowDialog();

    private void ApplyWindowsStartup()
    {
        try { WindowsStartupManager.Apply(_settings.StartWithWindows); }
        catch (Exception ex) { _logger?.Warn($"Не удалось изменить автозапуск Windows: {ex.Message}"); }
    }

    private void UpdateConfigurationSummary()
    {
        ConfigurationSummaryText.Text = "Управление папками и PACS";
        var ready = IsConfigurationReady();
        ConfigurationRequiredPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        EmptyQueuePanel.Visibility = ready && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = ready || _monitorTask is not null;
    }

    private bool IsConfigurationReady()
    {
        try
        {
            _settings.Copy().Validate();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private void PopulatePacsFilter()
    {
        if (PacsFilterCombo is null) return;
        var selected = (PacsFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        PacsFilterCombo.Items.Clear();
        PacsFilterCombo.Items.Add(new ComboBoxItem { Content = "Все" });
        foreach (var pacs in _settings.PacsServers.Where(p => p.Enabled))
            PacsFilterCombo.Items.Add(new ComboBoxItem { Content = pacs.Name });
        PacsFilterCombo.SelectedItem = PacsFilterCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => Equals(x.Content?.ToString(), selected)) ?? PacsFilterCombo.Items[0];
    }

    private Dictionary<string, DataGridColumn> ColumnsByKey() => new()
    {
        ["file"] = FileColumn, ["patient"] = PatientColumn, ["modality"] = ModalityColumn,
        ["status"] = StatusColumn, ["attempts"] = AttemptsColumn, ["time"] = TimeColumn,
        ["error"] = ErrorColumn, ["pacs"] = PacsColumn
    };

    private void ApplyColumnSettings()
    {
        foreach (var (key, column) in ColumnsByKey())
        {
            if (_settings.VisibleColumns.TryGetValue(key, out var visible))
                column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (_settings.ColumnWidths.TryGetValue(key, out var width) && width >= 30)
                column.Width = new DataGridLength(width);
        }
    }

    private void SaveColumnSettings()
    {
        foreach (var (key, column) in ColumnsByKey())
        {
            _settings.VisibleColumns[key] = column.Visibility == Visibility.Visible;
            if (column.ActualWidth >= 30) _settings.ColumnWidths[key] = column.ActualWidth;
        }
    }

    private void ColumnsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        var menu = new ContextMenu();
        foreach (var (key, column) in ColumnsByKey())
        {
            var item = new MenuItem { Header = column.Header?.ToString(), IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveColumnSettings();
                try { _settingsStore.Save(_settings); } catch (Exception ex) { _logger?.Warn($"Не удалось сохранить вид столбцов: {ex.Message}"); }
            };
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!_isUiInitialized) return;
        if (SearchHintText is not null)
            SearchHintText.Visibility = string.IsNullOrEmpty(SearchTextBox?.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshQueue();
    }

    private void UpdateRuntimeStatusPanel()
    {
        RuntimeStatusPanel.Children.Clear();
        if (_monitor is null) return;
        foreach (var target in _monitor.GetRuntimeStatuses())
        {
            var indicator = target.Type == "Pacs"
                ? target.Active ? target.Available switch { true => "●", false => "●", _ => "○" } : "Ⅱ"
                : target.Active ? "▶" : "Ⅱ";
            var color = !target.Active ? "#8A8F96" : target.Available switch
            {
                true => "#3D9B53",
                false => "#C75B5B",
                _ => "#718096"
            };
            var button = new System.Windows.Controls.Button
            {
                Content = $"{indicator} {target.Name}",
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = target.Type == "Pacs"
                    ? $"PACS: {(target.Active ? target.Available == true ? "доступен" : target.Available == false ? "недоступен" : "ещё не проверен" : "приостановлен")}. Нажмите для паузы/продолжения."
                    : $"Папка: {(target.Active ? "активна" : "приостановлена")}. Нажмите для паузы/продолжения."
            };
            button.Click += (_, _) =>
            {
                if (target.Type == "Pacs") _monitor.TogglePacsPause(target.Id);
                else _monitor.ToggleFolderPause(target.Id);
                UpdateRuntimeStatusPanel();
            };
            RuntimeStatusPanel.Children.Add(button);
        }
    }

    private void RetryFailedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        if (MessageBox.Show("Повторить все неуспешные доставки?", "DicomMover", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { var count = _database.RetryFailedAndStuck(); _logger?.Info($"Повторно поставлено в очередь: {count}."); RefreshQueue(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private QueueRow? SelectedQueueRow => QueueGrid.SelectedItem as QueueRow;

    private void QueueGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowSelectedDetails();
    private void DetailsMenuItem_Click(object sender, RoutedEventArgs e) => ShowSelectedDetails();

    private void ShowSelectedDetails()
    {
        if (_database is null || SelectedQueueRow is not { } row) return;
        try { new StudyDetailsWindow(_database, _logger, row.Id) { Owner = this }.ShowDialog(); RefreshQueue(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Подробности", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RetryItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedQueueRow is not { } row) return;
        var affected = _database.RetryQueueItem(row.Id);
        var item = _database.GetItem(row.Id);
        var identity = !string.IsNullOrWhiteSpace(item?.SopInstanceUid) ? $"SOP Instance UID {item.SopInstanceUid}" : row.FilePath;
        if (affected == 0)
            _logger?.Info($"Повторная отправка не требуется: исследование уже отправлено; {identity}.");
        else
            _logger?.Info($"Исследование повторно поставлено в очередь; {identity}.");
        RefreshQueue();
    }

    private void OpenFileLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueRow is not { } row) return;
        var directory = Path.GetDirectoryName(row.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = File.Exists(row.FilePath) ? $"/select,\"{row.FilePath}\"" : $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    private void CopyErrorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueRow is { Result.Length: > 0 } row) System.Windows.Clipboard.SetText(row.Result);
    }

    private void HideItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedQueueRow is not { } row) return;
        _database.ArchiveItem(row.Id);
        RefreshQueue();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        var dialog = new HistoryCleanupWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var count = dialog.HideAllRecords
                ? _database.ArchiveAllHistory()
                : _database.ArchiveSentHistory();
            _logger?.Info(dialog.HideAllRecords
                ? $"Из интерфейса скрыты все записи: {count}."
                : $"Из интерфейса скрыты успешные записи: {count}.");
            RefreshQueue();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Очистить журнал в интерфейсе?\n\nДневные файлы журналов сохранятся.",
                "DicomMover",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _visibleLogLines.Clear();
        LogList.Items.Clear();
        LogCountText.Text = "0 записей";
        LogErrorBadge.Visibility = Visibility.Collapsed;
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logger?.CurrentLogPath ?? Path.GetFullPath(_settings.LogFile))!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Журнал", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RefreshQueue()
    {
        var ready = IsConfigurationReady();
        ConfigurationRequiredPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready) EmptyQueuePanel.Visibility = Visibility.Collapsed;
        if (_database is null) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-_settings.UiRetentionHours);
            var counts = _database.GetCounts(cutoff);
            PendingCountText.Text = (counts.Pending + counts.Sending).ToString();
            SentCountText.Text = counts.Sent.ToString();
            FailedCountText.Text = counts.Failed.ToString();
            var daily = _database.GetDailySummary(DateTime.Now);
            DailySummaryText.Text = $"Сегодня: найдено {daily.Found}, отправлено {daily.Sent}, с ошибками {daily.WithErrors}";
            var search = SearchTextBox?.Text.Trim() ?? "";
            var status = (StatusFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var pacs = (PacsFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var items = _database.GetRecent(500, cutoff).Where(item =>
                (string.IsNullOrWhiteSpace(search) || new[] { item.FilePath, item.PatientName, item.SopInstanceUid, item.StudyInstanceUid, item.Modality, item.LastError }
                    .Any(value => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)) &&
                (status is null or "Все" || QueueRow.FromItem(item).Status == status) &&
                (pacs is null or "Все" || item.DeliverySummary?.Contains(pacs, StringComparison.CurrentCultureIgnoreCase) == true));
            _rows.Clear();
            foreach (var item in items) _rows.Add(QueueRow.FromItem(item));
            EmptyQueuePanel.Visibility = ready && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { _logger?.Error($"Не удалось обновить очередь: {ex.Message}"); }
    }

    private void AppendVisibleLog(string line)
    {
        _visibleLogLines.Enqueue((DateTime.Now, line));
        LogCountText.Text = $"{_visibleLogLines.Count} записей";
        if (LogLineMatchesFilter(line)) AddLogLineToView(line);
        if ((line.Contains("[ERROR]", StringComparison.Ordinal) ||
             line.Contains("[WARN]", StringComparison.Ordinal)) && !LogExpander.IsExpanded)
            LogErrorBadge.Visibility = Visibility.Visible;
        TrimVisibleLog();
        if (_trayIcon is not null &&
            (line.Contains("[ERROR]", StringComparison.Ordinal) ||
             line.Contains("[WARN]", StringComparison.Ordinal) ||
             line.Contains("лимит", StringComparison.OrdinalIgnoreCase)) &&
            DateTime.UtcNow - _lastTrayNotificationUtc > TimeSpan.FromMinutes(5))
        {
            _lastTrayNotificationUtc = DateTime.UtcNow;
            _trayIcon.ShowBalloonTip(5000, "DicomMover", line.Length > 220 ? line[..220] : line, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private void TrimVisibleLog()
    {
        var cutoff = DateTime.Now.AddHours(-_settings.UiRetentionHours);
        var changed = false;
        while (_visibleLogLines.TryPeek(out var entry) && entry.Timestamp < cutoff) { _visibleLogLines.Dequeue(); changed = true; }
        if (changed)
        {
            LogCountText.Text = $"{_visibleLogLines.Count} записей";
            RebuildLogView();
        }
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitialized) RebuildLogView();
    }

    private void LogExpander_Expanded(object sender, RoutedEventArgs e) =>
        LogErrorBadge.Visibility = Visibility.Collapsed;

    private void RebuildLogView()
    {
        if (LogList is null) return;
        LogList.Items.Clear();
        foreach (var entry in _visibleLogLines.Where(entry => LogLineMatchesFilter(entry.Line)))
            AddLogLineToView(entry.Line, false);
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private bool LogLineMatchesFilter(string line)
    {
        var filter = (LogFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return filter switch
        {
            "Ошибки" => line.Contains("[ERROR]", StringComparison.Ordinal),
            "Предупреждения" => line.Contains("[WARN]", StringComparison.Ordinal),
            "Информация" => line.Contains("[INFO]", StringComparison.Ordinal),
            _ => true
        };
    }

    private void AddLogLineToView(string line, bool scroll = true)
    {
        var color = line.Contains("[ERROR]", StringComparison.Ordinal) ? "#B42318"
            : line.Contains("[WARN]", StringComparison.Ordinal) ? "#B54708"
            : "#344054";
        var text = new TextBlock
        {
            Text = line,
            Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(4, 2, 4, 2)
        };
        LogList.Items.Add(text);
        if (scroll) LogList.ScrollIntoView(text);
    }

    private void InitializeTray()
    {
        try
        {
            var iconResource = System.Windows.Application.GetResourceStream(
                new Uri("Assets/dicommover.ico", UriKind.Relative));
            if (iconResource is null) throw new InvalidDataException("Ресурс иконки приложения не найден.");
            using var embeddedIcon = new System.Drawing.Icon(iconResource.Stream);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(RequestExit));
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = (System.Drawing.Icon)embeddedIcon.Clone(),
                Text = "DicomMover",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        }
        catch { }
    }

    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void RequestExit() { _exitRequested = true; Close(); }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _exitRequested = true;
        _logger?.Info("Получена команда завершения Windows. Останавливаем мониторинг.");
        _cts?.Cancel();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (!_exitRequested && _monitorTask is not null &&
            MessageBox.Show("Мониторинг активен. Остановить его и выйти?", "DicomMover", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (_monitorTask is not null)
        {
            e.Cancel = true;
            await StopMonitoringAsync();
            _exitRequested = true;
            Close();
            return;
        }
        _trayIcon?.Icon?.Dispose();
        _trayIcon?.Dispose();
        SaveColumnSettings();
        try { _settingsStore.Save(_settings); } catch { }
        System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
        base.OnClosing(e);
    }
}
