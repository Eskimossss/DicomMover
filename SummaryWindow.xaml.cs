using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DicomMover.Services;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class SummaryWindow : Window
{
    private readonly Database _database;
    private bool _initialized;

    public SummaryWindow(Database database)
    {
        InitializeComponent();
        _database = database;
        FromDatePicker.SelectedDate = DateTime.Today;
        ToDatePicker.SelectedDate = DateTime.Today;
        _initialized = true;
        RefreshSummary();
    }

    private void DatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initialized) RefreshSummary();
    }

    private void TodayButton_Click(object sender, RoutedEventArgs e)
    {
        _initialized = false;
        FromDatePicker.SelectedDate = DateTime.Today;
        ToDatePicker.SelectedDate = DateTime.Today;
        _initialized = true;
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (FromDatePicker.SelectedDate is not { } from || ToDatePicker.SelectedDate is not { } to) return;
        if (from.Date > to.Date)
        {
            PeriodText.Text = "Начальная дата не может быть позже конечной.";
            PacsGrid.ItemsSource = null;
            return;
        }

        var summary = _database.GetPeriodSummary(from, to);
        FoundText.Text = summary.Found.ToString();
        SentText.Text = summary.Sent.ToString();
        ErrorsText.Text = summary.WithErrors.ToString();
        AttemptsText.Text = summary.Attempts.ToString();
        PeriodText.Text = from.Date == to.Date
            ? $"За {from:dd.MM.yyyy}"
            : $"С {from:dd.MM.yyyy} по {to:dd.MM.yyyy}";
        PacsGrid.ItemsSource = _database.GetPacsPeriodSummary(from, to);
        var fileResults = _database.GetFileResultsForPeriod(from, to);
        FileResultsHeading.Text = $"Файлы и результаты — {fileResults.Count}";
        FileResultsGrid.ItemsSource = fileResults;
    }

    private void FileResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileResultsGrid.SelectedItem is not FileResultSummary item) return;
        if (!File.Exists(item.FilePath))
        {
            MessageBox.Show(
                $"Файл больше не доступен по сохранённому пути:\n\n{item.FilePath}",
                "Файл не найден",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть расположение файла", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
