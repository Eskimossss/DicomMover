using System.Windows;
using DicomMover.Models;
using DicomMover.Services;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class StudyDetailsWindow : Window
{
    private readonly Database _database;
    private readonly AppLogger? _logger;
    private readonly long _itemId;

    public StudyDetailsWindow(Database database, AppLogger? logger, long itemId)
    {
        InitializeComponent();
        _database = database;
        _logger = logger;
        _itemId = itemId;
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        var item = _database.GetItem(_itemId) ?? throw new InvalidOperationException("Запись не найдена.");
        PatientNameText.Text = item.PatientName ?? string.Empty;
        SopUidText.Text = item.SopInstanceUid ?? string.Empty;
        FilePathText.Text = item.FilePath;
        StatusText.Text = QueueRow.FromItem(item).Status;
        LastErrorText.Text = item.LastError ?? string.Empty;
        PatientIdText.Text = item.PatientId ?? string.Empty;
        BirthDateText.Text = FormatDicomDate(item.PatientBirthDate);
        ModalityText.Text = item.Modality ?? string.Empty;
        StudyUidText.Text = item.StudyInstanceUid ?? string.Empty;
        AccessionText.Text = item.AccessionNumber ?? string.Empty;
        StudyDateText.Text = FormatDicomDate(item.StudyDate);
        DeliveriesGrid.ItemsSource = _database.GetDeliveryDetails(_itemId);
    }

    private void RetrySelected_Click(object sender, RoutedEventArgs e)
    {
        if (DeliveriesGrid.SelectedItem is not DeliveryDetails delivery)
        {
            MessageBox.Show("Выберите PACS в таблице.", "Повторная отправка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var item = _database.GetItem(_itemId);
        var identity = !string.IsNullOrWhiteSpace(item?.SopInstanceUid) ? $"SOP Instance UID {item.SopInstanceUid}" : item?.FilePath ?? $"запись {_itemId}";
        if (_database.RetryDelivery(delivery.Id) == 0)
        {
            _logger?.Info($"Повторная отправка не требуется: исследование уже отправлено на {delivery.PacsName}; {identity}.");
            MessageBox.Show($"Исследование уже успешно отправлено на {delivery.PacsName}.", "Повторная отправка", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
            _logger?.Info($"Доставка повторно поставлена в очередь на {delivery.PacsName}; {identity}.");
        RefreshDetails();
    }

    private static string FormatDicomDate(string? value) =>
        value is { Length: 8 } && DateTime.TryParseExact(value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("dd.MM.yyyy")
            : value ?? string.Empty;
}
