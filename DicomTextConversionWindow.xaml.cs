using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DicomMover.Models;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class DicomTextConversionWindow : Window
{
    private readonly IReadOnlyList<DicomFileConversionResult> _allFiles;
    private readonly EncodingRule _rule;
    private List<DicomFileConversionResult> _visibleFiles = [];

    public DicomTextConversionWindow(IReadOnlyList<DicomFileConversionResult> files, EncodingRule rule)
    {
        InitializeComponent();
        _allFiles = files;
        _rule = rule;
        FileFilterCombo.ItemsSource = new[] { "Все файлы", "Только с ошибками" };
        ChangedFilterCombo.ItemsSource = new[] { "Все", "Да", "Нет" };
        ChangedFilterCombo.SelectedIndex = 0;
        FileFilterCombo.SelectedIndex = 0;
        AfterColumn.Header = $"После преобразования — {TargetText(rule.TargetEncoding)}";
        FolderSummaryText.Text = $"Файлов: {files.Count} • успешно: {files.Count(x => !x.HasProblem)} • с ошибками: {files.Count(x => x.HasProblem)} • кодировка: {TargetText(rule.TargetEncoding)}";
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        var currentPath = (FileCombo.SelectedItem as DicomFileConversionResult)?.FilePath;
        _visibleFiles = (FileFilterCombo.SelectedIndex == 1 ? _allFiles.Where(x => x.HasProblem) : _allFiles).ToList();
        FileCombo.ItemsSource = _visibleFiles;
        FileCombo.SelectedItem = _visibleFiles.FirstOrDefault(x => x.FilePath == currentPath) ?? _visibleFiles.FirstOrDefault();
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        if (FileCombo.SelectedItem is not DicomFileConversionResult file)
        {
            ElementsGrid.ItemsSource = null; DetailsButton.IsEnabled = false; PositionText.Text = "0 из 0";
            FileSummaryText.Text = "Подходящие файлы не найдены."; VerificationSummaryText.Text = string.Empty; return;
        }
        var preview = file.Preview;
        var required = (preview.RequiredFieldTagIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elements = preview.Elements.Where(x => IsSelected(x.TagId) || required.Contains(x.TagId)).ToList();
        var rows = elements.Select(source =>
        {
            var final = preview.FinalValues?.FirstOrDefault(x => x.Path == source.Path && x.TagId == source.TagId);
            return new ConversionElementRow
            {
                Source = source,
                Final = final,
                TargetCharset = preview.AfterCharset,
                FileResult = preview.Result,
                SelectedForRepair = IsSelected(source.TagId)
            };
        }).ToList();
        var visibleRows = ChangedFilterCombo.SelectedIndex switch
        {
            1 => rows.Where(x => x.Matches(ConversionChangedFilter.Changed)).ToList(),
            2 => rows.Where(x => x.Matches(ConversionChangedFilter.Unchanged)).ToList(),
            _ => rows
        };
        if (IgnoreEmptyCheck.IsChecked == true)
            visibleRows = visibleRows.Where(x => !x.IsEmpty).ToList();
        ElementsGrid.ItemsSource = visibleRows;
        ElementsGrid.SelectedItem = null;
        DetailsButton.IsEnabled = false;
        FileSummaryText.Text = $"Текстовых элементов: {rows.Count} • успешно: {rows.Count(x => !x.IsProblem)} • изменено значений: {rows.Count(x => x.Changed == "Да")} • ошибок: {rows.Count(x => x.IsProblem)}";
        VerificationSummaryText.Text = preview.Result;
        PositionText.Text = $"{_visibleFiles.IndexOf(file) + 1} из {_visibleFiles.Count}";
    }

    private bool IsSelected(string tagId) => _rule.RepairAllTextFields || _rule.SelectedTextFields is null ||
        _rule.SelectedTextFields.Contains(tagId, StringComparer.OrdinalIgnoreCase);
    private void FileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCurrent();
    private void FileFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshFiles(); }
    private void ChangedFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshCurrent(); }
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
        DetailsButton.IsEnabled = ElementsGrid.SelectedItem is ConversionElementRow;
    private void ElementsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowDetails();
    private void ShowDetails()
    {
        if (ElementsGrid.SelectedItem is not ConversionElementRow row)
        {
            return;
        }
        new DicomElementDetailsWindow(DicomElementTechnicalDetails.From(row)) { Owner = this }.ShowDialog();
    }
    private static string TargetText(TargetDicomEncoding target) => target switch
    {
        TargetDicomEncoding.IsoIr144 => "ISO_IR 144 / ISO-8859-5",
        TargetDicomEncoding.IsoIr100 => "ISO_IR 100 / Latin-1",
        _ => "ISO_IR 192 / UTF-8"
    };
}
