using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly ObservableCollection<ProblemRow> _problems = new();
    private readonly UiLogBuffer _logBuffer = new();
    private readonly ObservableCollection<UiLogEntry> _visibleLogItems = new();
    private readonly DispatcherTimer _timer;
    private readonly SettingsStore _settingsStore;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayMonitorItem;
    private AppSettings _settings = new();
    private AppLogger? _logger;
    private Database? _database;
    private FolderMonitor? _monitor;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _exitRequested;
    private bool _isUiInitialized;
    private DateTime _lastTrayNotificationUtc;
    private readonly HashSet<string> _knownProblemKeys = new(StringComparer.Ordinal);
    private int _unreadProblemCount;
    private bool _problemTrackingInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _isUiInitialized = true;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.SetCurrentDirectory(AppPaths.BaseDirectory);
        new DicomSetupBuilder().RegisterServices(services => services.AddFellowOakDicom()).Build();
        var configPath = AppPaths.Resolve("appsettings.json");
        _settingsStore = new SettingsStore(configPath);

        try
        {
            if (!File.Exists(configPath))
            {
                _settings = AppSettings.CreateDefaultSafe();
                _settingsStore.Save(_settings);
            }
            else
            {
                _settings = _settingsStore.Load();
            }

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
        ProblemsGrid.ItemsSource = _problems;
        LogList.ItemsSource = _visibleLogItems;
        PopulatePacsFilter();
        ApplyColumnSettings();
        UpdateConfigurationSummary();
        InitializeTray();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += UiTimer_Tick;
        _timer.Start();
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateJournalBounds();
        System.Windows.Application.Current.SessionEnding += Application_SessionEnding;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && _settings.MinimizeToTray) Hide(); };
        RefreshQueue();
        RefreshProblems();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateJournalBounds();
        JournalRow.Height = LogExpander.IsExpanded
            ? new GridLength(Math.Clamp(_settings.JournalHeight, 120, JournalRow.MaxHeight))
            : GridLength.Auto;
        if (MainTabControl.SelectedIndex < 0)
            MainTabControl.SelectedIndex = 0;
        RefreshQueue();
        if (_settings.StartMinimized) { WindowState = WindowState.Minimized; if (_settings.MinimizeToTray) Hide(); }
        if (_settings.AutoStartMonitoring) await StartMonitoringAsync(false);
    }

    private void BuildServices(AppSettings runtimeSettings)
    {
        if (_logger is not null) _logger.MessageWritten -= Logger_MessageWritten;
        if (_monitor is not null) _monitor.PacsAvailabilityChanged -= Monitor_PacsAvailabilityChanged;
        runtimeSettings.Validate();
        runtimeSettings.DatabaseFile = AppPaths.Resolve(runtimeSettings.DatabaseFile);
        runtimeSettings.LogFile = AppPaths.Resolve(runtimeSettings.LogFile);
        _logger = new AppLogger(runtimeSettings.LogFile);
        _logger.CleanupOldLogs(runtimeSettings.LogRetentionDays);
        _logger.MessageWritten += Logger_MessageWritten;
        _database = new Database(runtimeSettings.DatabaseFile);
        _database.ApplyStudyDateFilters(runtimeSettings.WatchFolders);
        MaintainDatabase(runtimeSettings);
        _monitor = new FolderMonitor(runtimeSettings, _database, _logger);
        _monitor.PacsAvailabilityChanged += Monitor_PacsAvailabilityChanged;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        RefreshQueue();
        RefreshProblems();
        TrimVisibleLog();
        UpdateRuntimeStatusPanel();
        UpdateTrayStatus();
    }

    private void Logger_MessageWritten(string line) => Dispatcher.BeginInvoke(() => AppendVisibleLog(line));

    private void Monitor_PacsAvailabilityChanged(string name, bool available, string? error) =>
        Dispatcher.BeginInvoke(() => ShowPacsAvailabilityNotification(name, available, error));

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
            UpdateTrayStatus();
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

        var duplicatePacs = _settings.FindAllDuplicatePacs();
        foreach (var (dup, orig) in duplicatePacs)
        {
            hasErrors = true;
            lines.Add($"✗ PACS «{dup.Name}»: дублирует параметры «{orig.Name}» ({dup.IpAddress}:{dup.Port}, {dup.CalledAeTitle})");
        }

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
            UpdateTrayStatus();
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
        await OpenSettingsAsync();
    }

    private async Task OpenSettingsAsync(Action<SettingsWindow>? configureWindow = null)
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        window.RequestSendHistoricalStudies += pacs =>
        {
            Dispatcher.BeginInvoke(() => OpenHistoryWizard(pacs.Id, window));
        };
        configureWindow?.Invoke(window);
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
        ["number"] = NumberColumn, ["file"] = FileColumn, ["patient"] = PatientColumn, ["modality"] = ModalityColumn,
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
            var color = target switch
            {
                { Active: false } => "#DC2626",
                { Type: "Folder" } => "#15803D",
                { Available: true } => "#15803D",
                { Available: false } => "#DC2626",
                _ => "#667085"
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
        try { var count = _database.RetryFailedAndStuck(); _monitor?.WakeDeliveryLoop(); _logger?.Info($"Повторно поставлено в очередь: {count}."); RefreshQueue(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private QueueRow? SelectedQueueRow => QueueGrid.SelectedItem as QueueRow;

    private void QueueGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = FindDataGridRow(QueueGrid, e.OriginalSource as DependencyObject);
        if (row?.Item is QueueRow queueRow) ShowDetails(queueRow);
    }
    private void DetailsMenuItem_Click(object sender, RoutedEventArgs e) => ShowSelectedDetails();

    private void ShowSelectedDetails()
    {
        if (_database is null || SelectedQueueRow is not { } row) return;
        ShowDetails(row);
    }

    private void ShowDetails(QueueRow row)
    {
        ShowDetails(row.Id);
    }

    private void ShowDetails(long queueItemId)
    {
        if (_database is null) return;
        try { new StudyDetailsWindow(_database, _logger, queueItemId) { Owner = this }.ShowDialog(); RefreshQueue(); RefreshProblems(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Подробности", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DataGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = FindDataGridRow(grid, e.OriginalSource as DependencyObject);
        if (row is null) return;
        grid.SelectedItem = row.Item;
        row.Focus();
    }

    private static DataGridRow? FindDataGridRow(DataGrid grid, DependencyObject? source) =>
        source is null ? null : ItemsControl.ContainerFromElement(grid, source) as DataGridRow;

    private void RetryItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedQueueRow is not { } row) return;
        var affected = _database.RetryQueueItem(row.Id);
        _monitor?.WakeDeliveryLoop();
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

    private void OpenHistoryWizard(string? preselectedPacsId = null, Window? owner = null)
    {
        if (_database is null) return;
        var wizard = new HistorySendingWizardWindow(_settings, _database, preselectedPacsId) { Owner = owner ?? this };
        if (wizard.ShowDialog() == true && wizard.DeliveriesCreated)
        {
            _monitor?.WakeDeliveryLoop();
            RefreshQueue();
            _logger?.Info(RussianPluralization.FormatStudiesEnqueued(wizard.EnqueuedCount));
        }
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

        _logBuffer.Clear();
        _visibleLogItems.Clear();
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
            var selectedId = SelectedQueueRow?.Id;
            var cutoff = DateTime.UtcNow.AddHours(-_settings.UiRetentionHours);
            var search = SearchTextBox?.Text.Trim() ?? "";
            var status = (StatusFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var pacs = (PacsFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var items = _database.GetRecent(500, cutoff).Where(item =>
                (string.IsNullOrWhiteSpace(search) || new[] { item.FilePath, item.PatientName, item.SopInstanceUid, item.StudyInstanceUid, item.Modality, item.LastError }
                    .Any(value => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)) &&
                (status is null or "Все" || QueueRow.FromItem(item).Status == status) &&
                (pacs is null or "Все" || item.DeliverySummary?.Contains(pacs, StringComparison.CurrentCultureIgnoreCase) == true));
            _rows.Clear();
            var rowNumber = 1;
            foreach (var item in items)
            {
                var row = QueueRow.FromItem(item);
                row.RowNumber = rowNumber++;
                _rows.Add(row);
            }
            if (selectedId is { } queueItemId)
                QueueGrid.SelectedItem = _rows.FirstOrDefault(row => row.Id == queueItemId);
            EmptyQueuePanel.Visibility = ready && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { _logger?.Error($"Не удалось обновить очередь: {ex.Message}"); }
    }

    private void CheckConfigurationProblems()
    {
        if (_database is null) return;
        var duplicates = _settings.FindAllDuplicatePacs();
        var activeDuplicateIds = duplicates.Select(d => d.Duplicate.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var activeProblem in _database.GetActiveProblems())
        {
            if (activeProblem.Key.StartsWith("config:duplicate_pacs:", StringComparison.Ordinal))
            {
                var pacsId = activeProblem.Key["config:duplicate_pacs:".Length..];
                if (!activeDuplicateIds.Contains(pacsId))
                {
                    _database.ResolveProblem(activeProblem.Key);
                }
            }
        }

        foreach (var (dup, orig) in duplicates)
        {
            _database.ReportProblem(
                $"config:duplicate_pacs:{dup.Id}",
                "Configuration",
                $"Дубликат PACS: «{dup.Name}»",
                $"PACS «{dup.Name}» имеет те же параметры ({dup.IpAddress}:{dup.Port}, {dup.CalledAeTitle}), что и «{orig.Name}». Измените параметры или удалите дубликат.",
                targetId: dup.Id);
        }
    }

    private void RefreshProblems()
    {
        if (_database is null)
        {
            _problems.Clear();
            ClearProblemNotifications();
            EmptyProblemsPanel.Visibility = Visibility.Visible;
            return;
        }

        CheckConfigurationProblems();
        var selectedKey = SelectedProblem?.Key;
        var records = _database.GetActiveProblems();
        var currentKeys = records.Select(problem => problem.Key).ToHashSet(StringComparer.Ordinal);
        if (!_problemTrackingInitialized)
        {
            _unreadProblemCount = currentKeys.Count;
            _problemTrackingInitialized = true;
        }
        else
        {
            _unreadProblemCount += currentKeys.Count(key => !_knownProblemKeys.Contains(key));
        }
        _knownProblemKeys.Clear();
        _knownProblemKeys.UnionWith(currentKeys);
        _problems.Clear();
        foreach (var problem in records) _problems.Add(ProblemRow.FromRecord(problem));
        if (selectedKey is not null)
            ProblemsGrid.SelectedItem = _problems.FirstOrDefault(problem => problem.Key == selectedKey);
        if (MainTabControl.SelectedIndex == 1) _unreadProblemCount = 0;
        UpdateProblemBadge();
        EmptyProblemsPanel.Visibility = _problems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized) return;
        var showProblems = MainTabControl.SelectedIndex == 1;
        QueueNavigationButton.IsChecked = !showProblems;
        ProblemsNavigationButton.IsChecked = showProblems;
        ProblemsSummaryText.Visibility = showProblems ? Visibility.Visible : Visibility.Collapsed;
        QueueTabActionsPanel.Visibility = showProblems ? Visibility.Collapsed : Visibility.Visible;
        ProblemsTabActionsPanel.Visibility = showProblems ? Visibility.Visible : Visibility.Collapsed;
        if (showProblems) ClearProblemNotifications();
    }

    private void QueueNavigationButton_Click(object sender, RoutedEventArgs e) => MainTabControl.SelectedIndex = 0;

    private void ProblemsNavigationButton_Click(object sender, RoutedEventArgs e) => MainTabControl.SelectedIndex = 1;

    private void SummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        new SummaryWindow(_database, _settings) { Owner = this }.ShowDialog();
    }

    private void ClearProblemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || _problems.Count == 0) return;
        var answer = MessageBox.Show(
            "Убрать все текущие записи из списка активных проблем?\n\nФайлы не будут удалены, а отправки не будут запущены повторно.",
            "Очистить список проблем",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _database.ResolveAllProblems();
        ClearProblemNotifications();
        RefreshProblems();
    }

    private void ClearProblemNotifications()
    {
        _unreadProblemCount = 0;
        UpdateProblemBadge();
    }

    private void UpdateProblemBadge()
    {
        ProblemCountText.Text = _unreadProblemCount.ToString();
        ProblemBadge.Visibility = _unreadProblemCount == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private ProblemRow? SelectedProblem => ProblemsGrid.SelectedItem as ProblemRow;

    private void ProblemsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = FindDataGridRow(ProblemsGrid, e.OriginalSource as DependencyObject);
        if (row?.Item is ProblemRow problem) ShowProblemDetails(problem);
    }

    private void ProblemDetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProblem is { } problem) ShowProblemDetails(problem);
    }

    private void ShowProblemDetails(ProblemRow problem)
    {
        if (problem.QueueItemId is { } queueItemId)
        {
            ShowDetails(queueItemId);
            return;
        }

        MessageBox.Show(
            "Для этой активной проблемы нет связанной записи исследования.\n\n" +
            $"Тип: {problem.Type}\nОбъект: {problem.ObjectName}\n\n{problem.Details}",
            "Подробности исследования",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ResolveProblemAction_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedProblem is not { } problem) return;
        ResolveProblem(problem);
    }

    private void ResolveProblem(ProblemRow problem)
    {
        if (_database is null) return;
        if (problem.KindCode is "Encoding" or "EncodingIrrecoverable" or "EncodingAmbiguous")
        {
            ResolveEncodingProblem(problem);
            return;
        }

        ProblemActionMenuItem_Click(this, new RoutedEventArgs());
    }

    private void ResolveEncodingProblem(ProblemRow problem)
    {
        if (_database is null) return;

        var queueItem = problem.QueueItemId is { } qId ? _database.GetItem(qId) : null;
        var folder = queueItem?.FolderId is { } fId ? _settings.WatchFolders.FirstOrDefault(f => f.Id == fId) : null;
        var pacs = problem.TargetId is { } pId ? _settings.PacsServers.FirstOrDefault(p => p.Id == pId) : null;

        long deliveryId = 0;
        if (problem.Key.StartsWith("delivery:", StringComparison.OrdinalIgnoreCase))
        {
            _ = long.TryParse(problem.Key["delivery:".Length..], out deliveryId);
        }
        else if (queueItem is not null)
        {
            var deliveries = _database.GetDeliveryDetails(queueItem.Id);
            deliveryId = deliveries.FirstOrDefault(d => d.Status != "Отправлено")?.Id ?? 0;
        }

        var dialog = new ResolveEncodingProblemWindow(
            problem.ObjectName,
            problem.FilePath ?? queueItem?.FilePath ?? "—",
            problem.Details,
            folder?.Name ?? "Все папки",
            pacs?.Name ?? "PACS")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || !dialog.IsConfirmed)
            return;

        try
        {
            var sourceMode = dialog.SelectedSourceMode;
            var modeName = sourceMode switch
            {
                SourceEncodingMode.ForceUtf8 => "UTF-8",
                SourceEncodingMode.ForceIso88595 => "ISO-8859-5",
                _ => "Windows-1251"
            };

            if (dialog.IsPermanentRule && folder is not null && pacs is not null)
            {
                var rule = new EncodingRule
                {
                    Name = $"Авто: {folder.Name} → {pacs.Name} ({modeName})",
                    SourceFolderId = folder.Id,
                    DestinationPacsId = pacs.Id,
                    ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements,
                    SourceEncodingMode = sourceMode,
                    TargetEncoding = TargetDicomEncoding.IsoIr192,
                    RepairAllTextFields = true,
                    MatchModality = dialog.MatchModality,
                    Modality = dialog.ModalityValue,
                    MatchManufacturer = dialog.MatchManufacturer,
                    Manufacturer = dialog.ManufacturerValue,
                    MatchStationName = dialog.MatchStationName,
                    StationName = dialog.StationNameValue
                };
                _settings.EncodingRules.Add(rule);
                _settingsStore.Save(_settings);
                _logger?.Info($"Создано постоянное правило кодировки: «{rule.Name}».");
            }
            else if (deliveryId > 0)
            {
                _database.SetDeliveryEncodingOverride(deliveryId, sourceMode, TargetDicomEncoding.IsoIr192);
            }

            if (deliveryId > 0)
            {
                _database.RetryDelivery(deliveryId);
            }
            else if (queueItem is not null)
            {
                _database.RetryQueueItem(queueItem.Id);
            }

            _monitor?.WakeDeliveryLoop();
            _logger?.Info($"Инженер выбрал решение кодировки для «{problem.ObjectName}» ({modeName}). Исследование поставлено в очередь отправки.");

            RefreshProblems();
            RefreshQueue();

            MessageBox.Show(
                $"Решение кодировки применено ({modeName}). Исследование возвращено в очередь отправки.\n\n" +
                "Проблема будет автоматически закрыта после успешного завершения C-STORE.",
                "Решение принято",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось применить решение проблемы:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ProblemActionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedProblem is not { } problem) return;
        try
        {
            switch (problem.KindCode)
            {
                case "Encoding" or "EncodingIrrecoverable" or "EncodingAmbiguous":
                    ResolveEncodingProblem(problem);
                    break;
                case "Delivery" when problem.QueueItemId is { } queueItemId:
                    RetryProblem(problem, queueItemId);
                    break;
                case "Pacs" when problem.TargetId is { } pacsId:
                    var pacs = _settings.PacsServers.FirstOrDefault(item => item.Id == pacsId);
                    if (pacs is null) return;
                    var check = await CheckPacsBeforeStartAsync(pacs);
                    if (check.Available) _database.ResolveProblem(problem.Key);
                    else _database.ReportProblem(problem.Key, "Pacs", pacs.Name, check.Error ?? "PACS недоступен", targetId: pacs.Id);
                    MessageBox.Show(check.Available ? "PACS доступен." : $"PACS недоступен:\n{check.Error}", "C-ECHO",
                        MessageBoxButton.OK, check.Available ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    break;
                case "Folder" when !string.IsNullOrWhiteSpace(problem.FilePath):
                    _ = Directory.EnumerateFileSystemEntries(problem.FilePath).Take(1).ToList();
                    _database.ResolveProblem(problem.Key);
                    MessageBox.Show("Папка доступна.", "Проверка папки", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case "BadFile":
                    OpenLocation(problem.FilePath);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось выполнить действие", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshProblems();
        RefreshQueue();
    }

    private void ProblemRetryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedProblem is not { QueueItemId: { } queueItemId } problem) return;
        try
        {
            RetryProblem(problem, queueItemId);
            RefreshProblems();
            RefreshQueue();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось повторить отправку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RetryProblem(ProblemRow problem, long queueItemId)
    {
        if (_database is null) return;
        _database.RetryQueueItem(queueItemId);
        _monitor?.WakeDeliveryLoop();
        _logger?.Info($"Доставка повторно поставлена в очередь вручную: {problem.ObjectName}.");
    }

    private void ProblemOpenLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProblem is { } problem) OpenLocation(problem.FilePath);
    }

    private void ResolveProblemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedProblem is not { } problem) return;
        _database.ResolveProblem(problem.Key);
        RefreshProblems();
    }

    private static void OpenLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    private void AppendVisibleLog(string line)
    {
        var trimmed = _logBuffer.Add(line, out var entry);
        LogCountText.Text = $"{_logBuffer.Count} записей";

        if (trimmed)
        {
            RebuildLogView();
        }
        else if (LogLineMatchesFilter(line))
        {
            _visibleLogItems.Add(entry);
            if (_visibleLogItems.Count > 0) LogList.ScrollIntoView(_visibleLogItems[^1]);
        }

        if ((line.Contains("[ERROR]", StringComparison.Ordinal) ||
             line.Contains("[WARN]", StringComparison.Ordinal)) && !LogExpander.IsExpanded)
            LogErrorBadge.Visibility = Visibility.Visible;

        TrimVisibleLog();
        var isPacsAvailabilityMessage = line.Contains("PACS ", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("недоступен", StringComparison.OrdinalIgnoreCase);
        if (_trayIcon is not null && !isPacsAvailabilityMessage &&
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
        var changed = _logBuffer.TrimTime(TimeSpan.FromHours(_settings.UiRetentionHours));
        if (changed)
        {
            LogCountText.Text = $"{_logBuffer.Count} записей";
            RebuildLogView();
        }
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitialized) RebuildLogView();
    }

    private void LogExpander_Expanded(object sender, RoutedEventArgs e)
    {
        LogErrorBadge.Visibility = Visibility.Collapsed;
        JournalRow.MinHeight = 120;
        UpdateJournalBounds();
        JournalRow.Height = new GridLength(Math.Clamp(_settings.JournalHeight, 120, JournalRow.MaxHeight));
    }

    private void LogExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (JournalRow.ActualHeight >= 120) _settings.JournalHeight = JournalRow.ActualHeight;
        JournalRow.MinHeight = 0;
        JournalRow.Height = GridLength.Auto;
    }

    private void JournalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!LogExpander.IsExpanded || JournalRow.ActualHeight < 120) return;
        _settings.JournalHeight = Math.Clamp(JournalRow.ActualHeight, 120, JournalRow.MaxHeight);
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) { _logger?.Warn($"Не удалось сохранить высоту журнала: {ex.Message}"); }
    }

    private void UpdateJournalBounds()
    {
        if (JournalRow is null || RootLayout is null) return;
        // Резервируем реальную высоту шапки, минимальную высоту очереди и всю нижнюю панель.
        var rows = RootLayout.RowDefinitions;
        var reservedHeight = rows[0].ActualHeight + MainContentRow.MinHeight + rows[2].ActualHeight + FooterRow.ActualHeight + 8;
        JournalRow.MaxHeight = Math.Max(120, RootLayout.ActualHeight - reservedHeight);
        if (LogExpander?.IsExpanded == true && JournalRow.Height.IsAbsolute && JournalRow.Height.Value > JournalRow.MaxHeight)
            JournalRow.Height = new GridLength(JournalRow.MaxHeight);
    }

    private void RebuildLogView()
    {
        if (LogList is null) return;
        _visibleLogItems.Clear();
        var filter = (LogFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        foreach (var entry in _logBuffer.GetSnapshot(filter))
            _visibleLogItems.Add(entry);
        if (_visibleLogItems.Count > 0) LogList.ScrollIntoView(_visibleLogItems[^1]);
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
            _trayMonitorItem = new System.Windows.Forms.ToolStripMenuItem("Запустить мониторинг", null,
                (_, _) => Dispatcher.Invoke(() => MonitorToggleButton_Click(this, new RoutedEventArgs())));
            menu.Items.Add(_trayMonitorItem);
            menu.Items.Add("Настройки", null, (_, _) => Dispatcher.Invoke(() => SettingsButton_Click(this, new RoutedEventArgs())));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(RequestExit));
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = (System.Drawing.Icon)embeddedIcon.Clone(),
                Text = "DicomMover",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
            UpdateTrayStatus();
        }
        catch { }
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null) return;
        var running = _monitorTask is not null;
        var problemCount = _database?.GetActiveProblems().Count ?? 0;
        _trayIcon.Text = problemCount > 0
            ? $"DicomMover — {(running ? "работает" : "остановлен")}; проблем: {problemCount}"
            : $"DicomMover — {(running ? "мониторинг работает" : "мониторинг остановлен")}";
        if (_trayMonitorItem is not null)
            _trayMonitorItem.Text = running ? "Остановить мониторинг" : "Запустить мониторинг";
    }

    private void ShowPacsAvailabilityNotification(string name, bool available, string? error)
    {
        if (_trayIcon is null) return;
        var message = available
            ? $"Связь с PACS «{name}» восстановлена."
            : $"PACS «{name}» стал недоступен.{(string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error}")}";
        if (message.Length > 220) message = message[..220];
        _trayIcon.ShowBalloonTip(5000, "DicomMover", message,
            available ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        RefreshProblems();
        UpdateTrayStatus();
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
        _timer.Stop();
        _timer.Tick -= UiTimer_Tick;
        if (_logger is not null) _logger.MessageWritten -= Logger_MessageWritten;
        if (_monitor is not null) _monitor.PacsAvailabilityChanged -= Monitor_PacsAvailabilityChanged;
        SaveColumnSettings();
        try { _settingsStore.Save(_settings); } catch { }
        System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
        base.OnClosing(e);
    }
}
