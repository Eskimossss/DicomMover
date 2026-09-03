using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DicomMover.Models;
using DicomMover.Services;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class SummaryWindow : Window
{
    public sealed record PacsFilterChoice(string? Id, string Name);

    private readonly Database _database;
    private readonly AppSettings? _settings;
    private bool _initialized;

    public SummaryWindow(Database database, AppSettings? settings = null)
    {
        InitializeComponent();
        _database = database;
        _settings = settings;

        var choices = new List<PacsFilterChoice> { new(null, "Все PACS") };
        if (_settings is not null)
        {
            choices.AddRange(_settings.PacsServers.Select(p => new PacsFilterChoice(p.Id, p.Name)));
        }
        PacsFilterCombo.ItemsSource = choices;
        PacsFilterCombo.SelectedIndex = 0;

        FromDatePicker.SelectedDate = DateTime.Today;
        ToDatePicker.SelectedDate = DateTime.Today;
        _initialized = true;
        RefreshSummary();
    }

    private void DatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized) RefreshSummary();
    }

    private void PacsFilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
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
            FileResultsGrid.ItemsSource = null;
            return;
        }

        var selectedPacsId = (PacsFilterCombo.SelectedItem as PacsFilterChoice)?.Id;
        var summary = _database.GetPeriodSummary(from, to, selectedPacsId);
        FoundText.Text = summary.Found.ToString();
        SentText.Text = summary.Sent.ToString();
        ErrorsText.Text = summary.WithErrors.ToString();
        AttemptsText.Text = summary.Attempts.ToString();
        PeriodText.Text = from.Date == to.Date
            ? $"За {from:dd.MM.yyyy}"
            : $"С {from:dd.MM.yyyy} по {to:dd.MM.yyyy}";
        PacsGrid.ItemsSource = _database.GetPacsPeriodSummary(from, to, selectedPacsId);
        var fileResults = _database.GetFileResultsForPeriod(from, to, selectedPacsId);
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
