using System.Windows;
using System.Windows.Controls;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using CheckBox = System.Windows.Controls.CheckBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DicomMover;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings.Copy();
        PopulateControls();
    }

    private void PopulateControls()
    {
        DataContext = Result;
        PostActionColumn.ItemsSource = new[]
        {
            new { Value = PostSendAction.Keep, Name = "Оставить" },
            new { Value = PostSendAction.Delete, Name = "Удалить" },
            new { Value = PostSendAction.Archive, Name = "В архив" }
        };
        ScanIntervalText.Text = Result.ScanIntervalSeconds.ToString();
        StableTimeText.Text = Result.FileStableSeconds.ToString();
        RetryIntervalText.Text = Result.RetryFailedAfterSeconds.ToString();
        MaxAttemptsText.Text = Result.MaxSendAttempts.ToString();
        UiHoursText.Text = Result.UiRetentionHours.ToString();
        LogDaysText.Text = Result.LogRetentionDays.ToString();
        DatabaseDaysText.Text = Result.DatabaseRetentionDays.ToString();
        BackupDaysText.Text = Result.BackupRetentionDays.ToString();
        StartWithWindowsCheck.IsChecked = Result.StartWithWindows;
        AutoStartCheck.IsChecked = Result.AutoStartMonitoring;
        StartMinimizedCheck.IsChecked = Result.StartMinimized;
        MinimizeToTrayCheck.IsChecked = Result.MinimizeToTray;
        DetailedLogCheck.IsChecked = Result.DetailedLogging;
        LogPatientNamesCheck.IsChecked = Result.LogPatientNames;
        DatabaseIntegrityCheck.IsChecked = Result.EnableDatabaseIntegrityCheck;
        if (Result.WatchFolders.Count > 0) FoldersGrid.SelectedIndex = 0;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = new WatchFolderSettings { Name = $"Папка {Result.WatchFolders.Count + 1}" };
        Result.WatchFolders.Add(folder);
        RefreshFolders(folder);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersGrid.SelectedItem is not WatchFolderSettings folder) return;
        Result.WatchFolders.Remove(folder);
        RefreshFolders(Result.WatchFolders.FirstOrDefault());
    }

    private void AddPacs_Click(object sender, RoutedEventArgs e)
    {
        var pacs = new PacsSettings { Name = $"PACS {Result.PacsServers.Count + 1}" };
        Result.PacsServers.Add(pacs);
        PacsGrid.ItemsSource = null;
        PacsGrid.ItemsSource = Result.PacsServers;
        PacsGrid.SelectedItem = pacs;
        RefreshPacsChoices();
    }

    private void RemovePacs_Click(object sender, RoutedEventArgs e)
    {
        if (PacsGrid.SelectedItem is not PacsSettings pacs) return;
        Result.PacsServers.Remove(pacs);
        foreach (var folder in Result.WatchFolders) folder.PacsIds.RemoveAll(id => id == pacs.Id);
        PacsGrid.ItemsSource = null;
        PacsGrid.ItemsSource = Result.PacsServers;
        RefreshPacsChoices();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e) => Browse(false);
    private void BrowseArchive_Click(object sender, RoutedEventArgs e) => Browse(true);

    private void Browse(bool archive)
    {
        if (FoldersGrid.SelectedItem is not WatchFolderSettings folder) return;
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() != true) return;
        if (archive) folder.ArchiveFolder = dialog.FolderName; else folder.Path = dialog.FolderName;
        FoldersGrid.Items.Refresh();
    }

    private void FoldersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPacsChoices();

    private void RefreshPacsChoices()
    {
        FolderPacsPanel.Items.Clear();
        if (FoldersGrid.SelectedItem is not WatchFolderSettings folder) return;
        foreach (var pacs in Result.PacsServers)
        {
            var check = new CheckBox
            {
                Content = pacs.Name,
                IsChecked = folder.PacsIds.Contains(pacs.Id),
                Margin = new Thickness(4),
                Tag = pacs.Id
            };
            check.Checked += PacsChoiceChanged;
            check.Unchecked += PacsChoiceChanged;
            FolderPacsPanel.Items.Add(check);
        }
    }

    private void PacsChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check || check.Tag is not string id ||
            FoldersGrid.SelectedItem is not WatchFolderSettings folder) return;
        if (check.IsChecked == true)
        {
            if (!folder.PacsIds.Contains(id)) folder.PacsIds.Add(id);
        }
        else folder.PacsIds.Remove(id);
    }

    private async void Echo_Click(object sender, RoutedEventArgs e)
    {
        PacsGrid.CommitEdit();
        if (PacsGrid.SelectedItem is not PacsSettings pacs) return;
        try
        {
            DicomStatus? status = null;
            var request = new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status };
            var client = DicomClientFactory.Create(pacs.IpAddress, pacs.Port, false, pacs.CallingAeTitle, pacs.CalledAeTitle);
            client.ClientOptions.ConnectionTimeoutInMs = pacs.ConnectionTimeoutSeconds * 1000;
            await client.AddRequestAsync(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(pacs.ConnectionTimeoutSeconds));
            await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
            MessageBox.Show(status?.State == DicomState.Success ? "PACS доступен." : $"PACS ответил: {status}", "C-ECHO");
        }
        catch (Exception ex) { MessageBox.Show($"PACS недоступен:\n{ex.Message}", "C-ECHO", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        FoldersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PacsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            ApplyControlValues();
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ApplyControlValues()
    {
        Result.ScanIntervalSeconds = Parse(ScanIntervalText.Text, "интервал проверки");
        Result.FileStableSeconds = Parse(StableTimeText.Text, "время стабильности");
        Result.RetryFailedAfterSeconds = Parse(RetryIntervalText.Text, "интервал повтора");
        Result.MaxSendAttempts = Parse(MaxAttemptsText.Text, "количество попыток");
        Result.UiRetentionHours = Parse(UiHoursText.Text, "время истории");
        Result.LogRetentionDays = Parse(LogDaysText.Text, "срок журналов");
        Result.DatabaseRetentionDays = Parse(DatabaseDaysText.Text, "срок истории SQLite");
        Result.BackupRetentionDays = Parse(BackupDaysText.Text, "срок резервных копий");
        Result.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        Result.AutoStartMonitoring = AutoStartCheck.IsChecked == true;
        Result.StartMinimized = StartMinimizedCheck.IsChecked == true;
        Result.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        Result.DetailedLogging = DetailedLogCheck.IsChecked == true;
        Result.LogPatientNames = LogPatientNamesCheck.IsChecked == true;
        Result.EnableDatabaseIntegrityCheck = DatabaseIntegrityCheck.IsChecked == true;
        Result.Validate();
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyControlValues();
            var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "dicommover-settings.json" };
            if (dialog.ShowDialog() == true) new SettingsStore(dialog.FileName).Save(Result.Copy());
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return;
            Result = new SettingsStore(dialog.FileName).Load();
            PopulateControls();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Импорт", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void BackupDatabase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyControlValues();
            var dialog = new SaveFileDialog { Filter = "SQLite (*.db)|*.db", FileName = $"dicommover-{DateTime.Now:yyyy-MM-dd}.db" };
            if (dialog.ShowDialog() != true) return;
            new Database(AppPaths.Resolve(Result.DatabaseFile)).BackupTo(dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Резервная копия", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static int Parse(string value, string name) =>
        int.TryParse(value, out var result) ? result : throw new InvalidDataException($"Некорректное значение: {name}.");

    private void RefreshFolders(WatchFolderSettings? selected)
    {
        FoldersGrid.ItemsSource = null;
        FoldersGrid.ItemsSource = Result.WatchFolders;
        FoldersGrid.SelectedItem = selected;
        RefreshPacsChoices();
    }
}
