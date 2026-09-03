using System.IO;
using System.Text;
using System.Windows;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;

namespace DicomMover;

public partial class ResolveEncodingProblemWindow : Window
{
    public SourceEncodingMode SelectedSourceMode { get; private set; } = SourceEncodingMode.ForceWindows1251;
    public bool IsPermanentRule { get; private set; }
    public bool IsConfirmed { get; private set; }

    public bool MatchModality => MatchModalityCheck.IsChecked == true && !string.IsNullOrWhiteSpace(ModalityBox.Text);
    public string ModalityValue => ModalityBox.Text.Trim();

    public bool MatchManufacturer => MatchManufacturerCheck.IsChecked == true && !string.IsNullOrWhiteSpace(ManufacturerBox.Text);
    public string ManufacturerValue => ManufacturerBox.Text.Trim();

    public bool MatchStationName => MatchStationNameCheck.IsChecked == true && !string.IsNullOrWhiteSpace(StationNameBox.Text);
    public string StationNameValue => StationNameBox.Text.Trim();

    public ResolveEncodingProblemWindow(
        string fileName,
        string filePath,
        string details,
        string folderName,
        string pacsName)
    {
        InitializeComponent();

        FileNameText.Text = fileName;
        FilePathText.Text = filePath;
        FolderAndPacsText.Text = $"Папка: «{folderName}»  →  PACS: «{pacsName}»";
        ErrorDetailsText.Text = details;

        if (!string.IsNullOrWhiteSpace(folderName) && !string.IsNullOrWhiteSpace(pacsName))
        {
            PermanentRadioText.Text = $"Создать постоянное правило для папки «{folderName}» и PACS «{pacsName}»";
        }

        LoadDicomDiagnostics(filePath);
    }

    private void LoadDicomDiagnostics(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ModalityText.Text = "—";
                ManufacturerText.Text = "—";
                StationNameText.Text = "—";
                ProblemTagText.Text = "Файл недоступен для чтения метаданных";
                return;
            }

            var file = DicomFile.Open(filePath, FileReadOption.ReadAll);
            var modality = file.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
            var manufacturer = file.Dataset.GetSingleValueOrDefault(DicomTag.Manufacturer, string.Empty);
            var station = file.Dataset.GetSingleValueOrDefault(DicomTag.StationName, string.Empty);

            ModalityText.Text = string.IsNullOrWhiteSpace(modality) ? "—" : modality;
            ManufacturerText.Text = string.IsNullOrWhiteSpace(manufacturer) ? "—" : manufacturer;
            StationNameText.Text = string.IsNullOrWhiteSpace(station) ? "—" : station;

            ModalityBox.Text = modality;
            ManufacturerBox.Text = manufacturer;
            StationNameBox.Text = station;

            if (!string.IsNullOrWhiteSpace(modality)) MatchModalityCheck.IsChecked = true;

            var analyzer = new DicomTextElementAnalyzer();
            var analyses = analyzer.Analyze(file, SourceEncodingMode.Automatic, Path.GetFileName(filePath));

            var problemElem = analyses.FirstOrDefault(a => a.Status is DicomTextElementStatus.Ambiguous or DicomTextElementStatus.Irrecoverable)
                              ?? analyses.FirstOrDefault(a => a.Status == DicomTextElementStatus.Recoverable)
                              ?? analyses.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.RawHex));

            if (problemElem is not null)
            {
                ProblemTagText.Text = $"{problemElem.Tag} {problemElem.FieldName}";
                RawBytesText.Text = string.IsNullOrWhiteSpace(problemElem.RawHex) ? "—" : problemElem.RawHex;
                DeclaredPreviewText.Text = $"Заявленная ({problemElem.DeclaredCharset}): {problemElem.DeclaredDecoding}";
                Cp1251PreviewText.Text = $"Windows-1251: {problemElem.Windows1251Decoding}";
                Iso88595PreviewText.Text = $"ISO-8859-5: {problemElem.Iso88595Decoding}";
                try
                {
                    var utf8Val = Encoding.UTF8.GetString(file.Dataset.GetDicomItem<DicomElement>(DicomTag.Parse(problemElem.TagId))?.Buffer.Data ?? Array.Empty<byte>());
                    Utf8PreviewText.Text = $"UTF-8: {utf8Val}";
                }
                catch
                {
                    Utf8PreviewText.Text = "UTF-8: —";
                }
            }
            else
            {
                ProblemTagText.Text = "Не найдено текстовых элементов с ошибками кодировки";
                RawBytesText.Text = "—";
            }
        }
        catch (Exception ex)
        {
            ProblemTagText.Text = $"Ошибка анализа тегов: {ex.Message}";
            RawBytesText.Text = "—";
        }
    }

    private void Radio_Changed(object sender, RoutedEventArgs e)
    {
        if (HardwareConditionsPanel is not null)
        {
            HardwareConditionsPanel.Visibility = PermanentRadio.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedSourceMode = SourceEncodingCombo.SelectedIndex switch
        {
            1 => SourceEncodingMode.ForceUtf8,
            2 => SourceEncodingMode.ForceIso88595,
            _ => SourceEncodingMode.ForceWindows1251
        };

        IsPermanentRule = PermanentRadio.IsChecked == true;
        IsConfirmed = true;
        DialogResult = true;
        Close();
    }
}
