using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DicomMover.Models;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class DicomTextDiagnosticWindow : Window
{
    private readonly IReadOnlyList<DicomFileDiagnosticResult> _allFiles;
    private List<DicomFileDiagnosticResult> _visibleFiles = [];

    public DicomTextDiagnosticWindow(IReadOnlyList<DicomFileDiagnosticResult> files)
    {
        InitializeComponent();
        _allFiles = files;
        FileFilterCombo.ItemsSource = new[] { "Все файлы", "Только с проблемами" };
        ElementFilterCombo.ItemsSource = new[] { "Все", "Требуют внимания", "Можно исправить" };
        ElementFilterCombo.SelectedIndex = 0;
        FileFilterCombo.SelectedIndex = 0;
        FolderSummaryText.Text = $"Файлов: {files.Count} • без проблем: {files.Count(x => !x.HasProblem)} • с проблемами: {files.Count(x => x.HasProblem)}";
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        var currentPath = (FileCombo.SelectedItem as DicomFileDiagnosticResult)?.FilePath;
        _visibleFiles = (FileFilterCombo.SelectedIndex == 1 ? _allFiles.Where(x => x.HasProblem) : _allFiles).ToList();
        FileCombo.ItemsSource = _visibleFiles;
        FileCombo.SelectedItem = _visibleFiles.FirstOrDefault(x => x.FilePath == currentPath) ?? _visibleFiles.FirstOrDefault();
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        if (FileCombo.SelectedItem is not DicomFileDiagnosticResult file)
        {
            ElementsGrid.ItemsSource = null; DetailsButton.IsEnabled = false; PositionText.Text = "0 из 0"; FileSummaryText.Text = "Подходящие файлы не найдены."; return;
        }
        var rows = file.Elements.Select(x => new DiagnosticElementRow { Source = x }).ToList();
        rows = ElementFilterCombo.SelectedIndex switch
        {
            1 => rows.Where(x => x.IsProblem).ToList(),
            2 => rows.Where(x => x.IsRepairable).ToList(),
            _ => rows
        };
        if (IgnoreEmptyCheck.IsChecked == true)
            rows = rows.Where(x => !x.IsEmpty).ToList();
        ElementsGrid.ItemsSource = rows;
        ElementsGrid.SelectedItem = null;
        DetailsButton.IsEnabled = false;
        var all = file.Elements.Select(x => new DiagnosticElementRow { Source = x }).ToList();
        FileSummaryText.Text = $"Текстовых элементов: {all.Count} • корректных: {all.Count(x => x.Status == "Корректно")} • можно исправить: {all.Count(x => x.IsRepairable)} • ошибок: {all.Count(x => x.IsProblem && !x.IsRepairable)}";
        PositionText.Text = $"{_visibleFiles.IndexOf(file) + 1} из {_visibleFiles.Count}";
    }

    private void FileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCurrent();
    private void FileFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshFiles(); }
    private void ElementFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshCurrent(); }
    private void IgnoreEmptyCheck_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) RefreshCurrent(); }
    private void Previous_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta)
    {
        if (_visibleFiles.Count == 0) return;
        FileCombo.SelectedIndex = Math.Clamp(FileCombo.SelectedIndex + delta, 0, _visibleFiles.Count - 1);
    }
    private void Details_Click(object sender, RoutedEventArgs e) => ShowDetails();
    private void ElementsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DetailsButton.IsEnabled = ElementsGrid.SelectedItem is DiagnosticElementRow;
    private void ElementsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowDetails();
    private void ShowDetails()
    {
        if (ElementsGrid.SelectedItem is not DiagnosticElementRow row)
        {
            return;
        }
        new DicomElementDetailsWindow(DicomElementTechnicalDetails.From(row)) { Owner = this }.ShowDialog();
    }
}
