using System.Windows;
using System.Windows.Controls;
using DicomMover.Models;
using DicomMover.Services;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class HistorySendingWizardWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Database _database;
    private bool _isInitialized;
    private int _foundCount;
    private long _totalBytes;
    private int _missingCount;
    private CancellationTokenSource? _cts;

    public bool DeliveriesCreated { get; private set; }
    public int EnqueuedCount { get; private set; }

    public sealed record FolderChoice(string? Id, string Name);

    public HistorySendingWizardWindow(
        AppSettings settings,
        Database database,
        string? preselectedPacsId = null)
    {
        InitializeComponent();

        _settings = settings;
        _database = database;

        // PACS list
        PacsCombo.ItemsSource = _settings.PacsServers;
        if (!string.IsNullOrWhiteSpace(preselectedPacsId))
        {
            var preselected = _settings.PacsServers.FirstOrDefault(p => p.Id == preselectedPacsId);
            if (preselected is null)
            {
                MessageBox.Show("Целевой PACS не найден или был удалён из настроек.", "Отправка сохранённых исследований", MessageBoxButton.OK, MessageBoxImage.Error);
                Loaded += (_, _) => Close();
                return;
            }
            if (!preselected.Enabled)
            {
                MessageBox.Show($"PACS «{preselected.Name}» отключён. Включите PACS перед отправкой сохранённых исследований.", "Отправка сохранённых исследований", MessageBoxButton.OK, MessageBoxImage.Warning);
                Loaded += (_, _) => Close();
                return;
            }
            PacsCombo.SelectedItem = preselected;
            PacsCombo.IsEnabled = false;
        }
        else if (PacsCombo.SelectedItem is null && _settings.PacsServers.Count > 0)
        {
            PacsCombo.SelectedIndex = 0;
        }

        // Folders list
        var folderChoices = new List<FolderChoice> { new(null, "Все отслеживаемые папки") };
        folderChoices.AddRange(_settings.WatchFolders.Select(f => new FolderChoice(f.Id, f.Name)));
        FolderCombo.ItemsSource = folderChoices;
        FolderCombo.SelectedIndex = 0;

        // Modalities list
        var modalityChoices = new List<string> { "Все модальности" };
        try
        {
            modalityChoices.AddRange(_database.GetDistinctModalities());
        }
        catch { }
        ModalityCombo.ItemsSource = modalityChoices;
        ModalityCombo.SelectedIndex = 0;

        _isInitialized = true;
        UpdatePreview();
    }

    private void FilterControl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialized)
            UpdatePreview();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        ConfirmCheckBox.IsChecked = false;

        if (PacsCombo.SelectedItem is not PacsSettings pacs)
        {
            SummaryCountText.Text = "Выберите целевой PACS для расчета количества исследований к отправке.";
            MissingFilesReportText.Visibility = Visibility.Collapsed;
            WarningPanel.Visibility = Visibility.Collapsed;
            PreviewGrid.ItemsSource = null;
            ConfirmCheckBox.IsEnabled = false;
            StartButton.IsEnabled = false;
            _foundCount = 0;
            _totalBytes = 0;
            return;
        }

        var folderId = (FolderCombo.SelectedItem as FolderChoice)?.Id;
        var modality = ModalityCombo.SelectedItem as string;
        if (modality == "Все модальности") modality = null;
        var fromDate = FromDatePicker.SelectedDate;
        var toDate = ToDatePicker.SelectedDate;

        try
        {
            var summary = _database.CalculateHistoricalStudies(folderId, fromDate, toDate, modality, pacs.Id);
            _foundCount = summary.EligibleCount;
            _totalBytes = summary.TotalBytes;
            _missingCount = summary.MissingFilesCount;

            PreviewGrid.ItemsSource = summary.PreviewItems;

            if (_missingCount > 0)
            {
                MissingFilesReportText.Text = $"Пропущено {PluralizeRecords(summary.MissingFilesCount)}, файлы которых отсутствуют на диске.";
                MissingFilesReportText.Visibility = Visibility.Visible;
            }
            else
            {
                MissingFilesReportText.Visibility = Visibility.Collapsed;
            }

            if (_foundCount == 0)
            {
                SummaryCountText.Text = $"Для PACS «{pacs.Name}» новых неотправленных исследований по выбранным фильтрам не найдено.";
                WarningPanel.Visibility = Visibility.Collapsed;
                ConfirmCheckBox.IsEnabled = false;
                StartButton.IsEnabled = false;
            }
            else
            {
                var sizeStr = FormatBytes(_totalBytes);
                var studiesStr = RussianPluralization.PluralizeStudies(_foundCount);
                SummaryCountText.Text = $"Найдено для отправки на «{pacs.Name}»: {studiesStr} ({sizeStr}). Показаны первые {summary.PreviewItems.Count}.";
                ConfirmText.Text = $"Подтверждаю отправку {studiesStr} ({sizeStr}) на PACS «{pacs.Name}»";
                ConfirmCheckBox.IsEnabled = true;

                WarningText.Text = $"Массовая отправка ({studiesStr}, {sizeStr}) на PACS «{pacs.Name}» ({pacs.IpAddress}:{pacs.Port}) " +
                                  "создаст сетевую нагрузку. Отправка будет происходить последовательно в фоновом режиме через стандартный монитор.";
                WarningPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            SummaryCountText.Text = $"Ошибка при расчете: {ex.Message}";
            MissingFilesReportText.Visibility = Visibility.Collapsed;
            WarningPanel.Visibility = Visibility.Collapsed;
            ConfirmCheckBox.IsEnabled = false;
            StartButton.IsEnabled = false;
        }
    }

    private void ConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = ConfirmCheckBox.IsChecked == true && _foundCount > 0;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (PacsCombo.SelectedItem is not PacsSettings pacs || _foundCount <= 0 || ConfirmCheckBox.IsChecked != true)
            return;

        var currentPacs = _settings.PacsServers.FirstOrDefault(p => p.Id == pacs.Id);
        if (currentPacs is null)
        {
            MessageBox.Show($"Целевой PACS «{pacs.Name}» был удалён из настроек.", "Отправка сохранённых исследований", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!currentPacs.Enabled)
        {
            MessageBox.Show($"Целевой PACS «{currentPacs.Name}» отключён. Включите PACS перед отправкой сохранённых исследований.", "Отправка сохранённых исследований", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderId = (FolderCombo.SelectedItem as FolderChoice)?.Id;
        var modality = ModalityCombo.SelectedItem as string;
        if (modality == "Все модальности") modality = null;
        var fromDate = FromDatePicker.SelectedDate;
        var toDate = ToDatePicker.SelectedDate;

        StartButton.IsEnabled = false;
        ConfirmCheckBox.IsEnabled = false;
        CancelButton.Content = "Прервать";
        EnqueueProgressBar.Visibility = Visibility.Visible;
        EnqueueProgressBar.IsIndeterminate = true;

        _cts = new CancellationTokenSource();

        try
        {
            var enqueued = await Task.Run(() =>
                _database.EnqueueHistoricalDeliveries(
                    folderId,
                    fromDate,
                    toDate,
                    modality,
                    pacs,
                    _cts.Token), _cts.Token);

            DeliveriesCreated = true;
            EnqueuedCount = enqueued;

            var missingMsg = _missingCount > 0 ? $"\nПропущено отсутствующих на диске файлов: {_missingCount}." : "";
            System.Windows.MessageBox.Show(
                $"Успешно добавлено в очередь отправки: {enqueued} исследований ({FormatBytes(_totalBytes)}) на PACS «{pacs.Name}».{missingMsg}\n\n" +
                "Отправка выполняется в фоновом режиме через стандартный монитор очереди.",
                "Отправка истории запущена",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            System.Windows.MessageBox.Show("Постановка исследований в очередь была прервана пользователем.", "Операция отменена", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdatePreview();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось поставить исследования в очередь:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts = null;
            EnqueueProgressBar.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Закрыть";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }
        Close();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ";
    }

    private static string PluralizeRecords(int count)
    {
        var abs = Math.Abs(count);
        var rem100 = abs % 100;
        var rem10 = abs % 10;
        if (rem100 is >= 11 and <= 14) return $"{count} записей";
        if (rem10 == 1) return $"{count} запись";
        if (rem10 is >= 2 and <= 4) return $"{count} записи";
        return $"{count} записей";
    }
}
