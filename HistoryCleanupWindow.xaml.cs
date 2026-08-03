using System.Windows;

namespace DicomMover;

public partial class HistoryCleanupWindow : Window
{
    public bool HideAllRecords => AllRecordsRadio.IsChecked == true;

    public HistoryCleanupWindow() => InitializeComponent();

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
