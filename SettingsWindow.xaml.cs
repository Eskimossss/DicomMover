using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using System.Text;
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

public sealed class ZeroToEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number == 0 ? string.Empty : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        int.TryParse(value?.ToString(), out var number) ? number : 0;
}

public sealed class PacsEndpointConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var address = values.ElementAtOrDefault(0)?.ToString();
        var port = values.ElementAtOrDefault(1) is int number ? number : 0;
        return string.IsNullOrWhiteSpace(address) || port <= 0 ? "Не настроен" : $"{address}:{port}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    private readonly string _initialState;
    private bool _saved;
    private DicomTextDiagnosticResult? _lastDiagnostic;
    private bool _updatingFieldSelection;
    private readonly HashSet<string> _requiredFieldTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DicomMetadataValues> _conditionValuesCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record SelectionChoice(string? Id, string Name);

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        if (SettingsTabs.Items.Count > 1 && SettingsTabs.Items[1] is TabItem pacsTab)
        {
            SettingsTabs.Items.RemoveAt(1);
            SettingsTabs.Items.Insert(0, pacsTab);
            SettingsTabs.SelectedIndex = 0;
        }
        Result = settings.Copy();
        Result.NormalizeLegacy();
        PopulateControls();
        _initialState = CaptureState();
    }

    public void OpenEncodingRule(string? sourceFolderId, string? destinationPacsId)
    {
        var encodingTab = SettingsTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Header?.ToString(), "Кодировка", StringComparison.Ordinal));
        if (encodingTab is not null) SettingsTabs.SelectedItem = encodingTab;

        var matchingRule = string.IsNullOrWhiteSpace(sourceFolderId) || string.IsNullOrWhiteSpace(destinationPacsId)
            ? null
            : Result.EncodingRules.FirstOrDefault(rule =>
                string.Equals(rule.SourceFolderId, sourceFolderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.DestinationPacsId, destinationPacsId, StringComparison.OrdinalIgnoreCase));

        // При неточном контексте не оставляем автоматически выбранное первое правило.
        EncodingRulesGrid.SelectedItem = matchingRule;
        if (matchingRule is not null) EncodingRulesGrid.ScrollIntoView(matchingRule);
        UpdateEncodingModeAvailability();
    }

    private void PopulateControls()
    {
        DataContext = Result;
        PostActionCombo.ItemsSource = new[]
        {
            new { Value = PostSendAction.Keep, Name = "Оставить" },
            new { Value = PostSendAction.Delete, Name = "Удалить" },
            new { Value = PostSendAction.Archive, Name = "В архив" }
        };
        ScanIntervalText.Text = Result.ScanIntervalSeconds.ToString();
        PacsHealthCheckText.Text = Result.PacsHealthCheckSeconds.ToString();
        StableTimeText.Text = Result.FileStableSeconds.ToString();
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
        SourceEncodingCombo.ItemsSource = new[]
        {
            new { Value = SourceEncodingMode.Automatic, Name = "Автоматически (рекомендуется)" },
            new { Value = SourceEncodingMode.ForceWindows1251, Name = "Принудительно Windows-1251" },
            new { Value = SourceEncodingMode.ForceUtf8, Name = "Принудительно UTF-8" },
            new { Value = SourceEncodingMode.ForceIso88595, Name = "Принудительно ISO-8859-5" }
        };
        var targetEncodings = new[]
        {
            new { Value = TargetDicomEncoding.NoChange, Name = "Не изменять — отправить оригинал" },
            new { Value = TargetDicomEncoding.IsoIr192, Name = "ISO_IR 192 — UTF-8 (рекомендуется)" },
            new { Value = TargetDicomEncoding.IsoIr144, Name = "ISO_IR 144 — кириллица ISO-8859-5" },
            new { Value = TargetDicomEncoding.IsoIr100, Name = "ISO_IR 100 — Latin-1, без кириллицы" }
        };
        RepairTargetEncodingCombo.ItemsSource = targetEncodings.Where(x => x.Value != TargetDicomEncoding.NoChange).ToArray();
        RefreshEncodingChoices();
        if (Result.WatchFolders.Count > 0) FoldersGrid.SelectedIndex = 0;
        if (Result.PacsServers.Count > 0) PacsGrid.SelectedIndex = 0;
        if (Result.EncodingRules.Count > 0) EncodingRulesGrid.SelectedIndex = 0;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = new WatchFolderSettings
        {
            Name = $"Папка {Result.WatchFolders.Count + 1}",
            Path = string.Empty
        };
        Result.WatchFolders.Add(folder);
        RefreshFolders(folder);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersGrid.SelectedItem is not WatchFolderSettings folder) return;
        if (MessageBox.Show(
                $"Удалить папку «{folder.Name}» из настроек?\n\nФайлы на диске удалены не будут.",
                "Удаление папки",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Result.WatchFolders.Remove(folder);
        foreach (var rule in Result.EncodingRules.Where(r => r.SourceFolderId == folder.Id).ToList())
            Result.EncodingRules.Remove(rule);
        RefreshFolders(Result.WatchFolders.FirstOrDefault());
    }

    private void AddPacs_Click(object sender, RoutedEventArgs e)
    {
        var pacs = new PacsSettings
        {
            Name = $"PACS {Result.PacsServers.Count + 1}",
            IpAddress = string.Empty,
            Port = 0,
            CalledAeTitle = string.Empty,
            CallingAeTitle = "DICOMMOVER",
            ConnectionTimeoutSeconds = 15,
            SendTimeoutSeconds = 120
        };
        Result.PacsServers.Add(pacs);
        PacsGrid.Items.Refresh();
        PacsGrid.SelectedItem = pacs;
        RefreshPacsChoices();
        RefreshEncodingChoices();
    }

    private void RemovePacs_Click(object sender, RoutedEventArgs e)
    {
        if (PacsGrid.SelectedItem is not PacsSettings pacs) return;
        if (MessageBox.Show(
                $"Удалить PACS «{pacs.Name}» из настроек?\n\nОн также будет удалён из назначений всех папок.",
                "Удаление PACS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Result.PacsServers.Remove(pacs);
        foreach (var rule in Result.EncodingRules.Where(r => r.DestinationPacsId == pacs.Id).ToList())
            Result.EncodingRules.Remove(rule);
        foreach (var folder in Result.WatchFolders) folder.PacsIds.RemoveAll(id => id == pacs.Id);
        PacsGrid.Items.Refresh();
        RefreshPacsChoices();
        RefreshEncodingChoices();
        EncodingRulesGrid.Items.Refresh();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e) => Browse(false);
    private void BrowseArchive_Click(object sender, RoutedEventArgs e) => Browse(true);

    private void Browse(bool archive)
    {
        if (FoldersGrid.SelectedItem is not WatchFolderSettings folder) return;
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() != true) return;
        if (archive)
        {
            folder.ArchiveFolder = dialog.FolderName;
            ArchiveFolderText.Text = dialog.FolderName;
        }
        else
        {
            folder.Path = dialog.FolderName;
            FolderPathText.Text = dialog.FolderName;
        }
        FoldersGrid.Items.Refresh();
    }

    private void FoldersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPacsChoices();
        UpdatePostActionAvailability();
    }

    private void PostActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePostActionAvailability();

    private void DeleteEmptySubfoldersCheck_Changed(object sender, RoutedEventArgs e) =>
        UpdatePostActionAvailability();

    private void UpdatePostActionAvailability()
    {
        if (ArchiveFolderPanel is null || FoldersGrid?.SelectedItem is not WatchFolderSettings folder) return;
        var action = PostActionCombo?.SelectedValue is PostSendAction selected ? selected : folder.PostSendAction;
        var archives = action == PostSendAction.Archive;
        var removesSource = action is PostSendAction.Archive or PostSendAction.Delete;
        ArchiveFolderPanel.IsEnabled = archives;
        ArchiveFolderLabel.IsEnabled = archives;
        PreserveSubfoldersCheck.IsEnabled = archives;
        DeleteEmptySubfoldersCheck.IsEnabled = removesSource;
        if (EmptyFolderDelayPanel is not null)
            EmptyFolderDelayPanel.IsEnabled = removesSource && (DeleteEmptySubfoldersCheck.IsChecked == true);
    }

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

    private void RefreshEncodingChoices()
    {
        if (EncodingFolderCombo is null) return;
        var folders = Result.WatchFolders.Select(f => new SelectionChoice(f.Id, f.Name)).ToList();
        EncodingFolderCombo.ItemsSource = folders;
        EncodingPacsCombo.ItemsSource = Result.PacsServers.Select(p => new SelectionChoice(p.Id, p.Name)).ToList();
    }

    private void AddEncodingRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = EncodingRule.CreateNew($"Правило {Result.EncodingRules.Count + 1}",
            Result.WatchFolders.FirstOrDefault()?.Id, Result.PacsServers.FirstOrDefault()?.Id ?? string.Empty);
        Result.EncodingRules.Add(rule);
        EncodingRulesGrid.Items.Refresh();
        EncodingRulesGrid.SelectedItem = rule;
        RefreshConditionValues();
    }

    private void RemoveEncodingRule_Click(object sender, RoutedEventArgs e)
    {
        if (EncodingRulesGrid.SelectedItem is not EncodingRule rule) return;
        Result.EncodingRules.Remove(rule);
        EncodingRulesGrid.Items.Refresh();
        EncodingRulesGrid.SelectedItem = Result.EncodingRules.FirstOrDefault();
    }

    private void EncodingRulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEncodingModeAvailability();
        RefreshConditionValues();
    }
    private void EncodingFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EncodingRulesGrid?.SelectedItem is EncodingRule rule && EncodingFolderCombo.SelectedValue is string folderId)
            rule.SourceFolderId = folderId;
        RefreshConditionValues();
    }
    private void RefreshConditionValues_Click(object sender, RoutedEventArgs e) => RefreshConditionValues(true);

    private void RefreshConditionValues(bool force = false)
    {
        if (ModalityConditionCombo is null || EncodingRulesGrid?.SelectedItem is not EncodingRule rule)
        {
            if (ModalityConditionCombo is not null)
            {
                ModalityConditionCombo.ItemsSource = null;
                StationNameConditionCombo.ItemsSource = null;
                ManufacturerConditionCombo.ItemsSource = null;
            }
            return;
        }

        var folderId = EncodingFolderCombo.SelectedValue as string ?? rule.SourceFolderId;
        var folder = Result.WatchFolders.FirstOrDefault(item =>
            string.Equals(item.Id, folderId, StringComparison.OrdinalIgnoreCase));
        var values = DicomMetadataValues.Empty;
        if (folder is not null)
        {
            try
            {
                if (force || !_conditionValuesCache.TryGetValue(folder.Id, out values!))
                {
                    System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                    values = new DicomMetadataDiscoveryService().Discover(folder);
                    _conditionValuesCache[folder.Id] = values;
                }
            }
            catch (Exception ex)
            {
                if (force) MessageBox.Show(ex.Message, "Обновление значений", MessageBoxButton.OK, MessageBoxImage.Warning);
                values = DicomMetadataValues.Empty;
            }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        }

        var modality = rule.Modality;
        var station = rule.StationName;
        var manufacturer = rule.Manufacturer;
        ModalityConditionCombo.ItemsSource = values.Modalities;
        StationNameConditionCombo.ItemsSource = values.StationNames;
        ManufacturerConditionCombo.ItemsSource = values.Manufacturers;
        ModalityConditionCombo.Text = modality ?? string.Empty;
        StationNameConditionCombo.Text = station ?? string.Empty;
        ManufacturerConditionCombo.Text = manufacturer ?? string.Empty;
        ModalityConditionCombo.ToolTip = values.Modalities.Count == 0 ? "В выбранной папке значения Modality не найдены." : $"Найдено значений: {values.Modalities.Count}";
        StationNameConditionCombo.ToolTip = values.StationNames.Count == 0 ? "В выбранной папке значения Station Name не найдены." : $"Найдено значений: {values.StationNames.Count}";
        ManufacturerConditionCombo.ToolTip = values.Manufacturers.Count == 0 ? "В выбранной папке значения Manufacturer не найдены." : $"Найдено значений: {values.Manufacturers.Count}";
    }
    private void SourceEncodingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void UpdateEncodingModeAvailability()
    {
        if (LegacyEncodingPanel is null) return;
        var hasRule = EncodingRulesGrid?.SelectedItem is EncodingRule;
        RepairFieldsPanel.IsEnabled = hasRule;
        TargetEncodingPanel.IsEnabled = hasRule;
        _requiredFieldTags.Clear();
        RequiredFieldsPanel.Visibility = Visibility.Collapsed;
        RefreshSelectedTextFields();
    }

    private IEnumerable<CheckBox> TextFieldChecks()
    {
        yield return PatientNameFieldCheck; yield return PatientIdFieldCheck; yield return InstitutionNameFieldCheck;
        yield return StudyDescriptionFieldCheck; yield return SeriesDescriptionFieldCheck; yield return ProtocolNameFieldCheck;
        yield return ReferringPhysicianFieldCheck; yield return PerformingPhysicianFieldCheck;
        yield return ReadingPhysicianFieldCheck; yield return OperatorsNameFieldCheck;
    }

    private void RefreshSelectedTextFields()
    {
        if (AllTextFieldsCheck is null || EncodingRulesGrid?.SelectedItem is not EncodingRule rule) return;
        _updatingFieldSelection = true;
        AllTextFieldsCheck.IsChecked = rule.RepairAllTextFields || rule.SelectedTextFields is null;
        foreach (var check in TextFieldChecks())
        {
            check.IsChecked = rule.SelectedTextFields?.Contains((string)check.Tag, StringComparer.OrdinalIgnoreCase) == true;
            check.IsEnabled = !AllTextFieldsCheck.IsChecked.GetValueOrDefault() && RepairFieldsPanel.IsEnabled;
        }
        _updatingFieldSelection = false;
    }

    private void TextFieldCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFieldSelection || EncodingRulesGrid.SelectedItem is not EncodingRule rule) return;
        rule.SelectedTextFields ??= [];
        if (sender is CheckBox check && check.Tag is string tag)
        {
            if (check.IsChecked == true && !rule.SelectedTextFields.Contains(tag, StringComparer.OrdinalIgnoreCase)) rule.SelectedTextFields.Add(tag);
            if (check.IsChecked != true) rule.SelectedTextFields.RemoveAll(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void AllTextFieldsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFieldSelection || EncodingRulesGrid.SelectedItem is not EncodingRule rule) return;
        rule.RepairAllTextFields = AllTextFieldsCheck.IsChecked == true;
        if (!rule.RepairAllTextFields && rule.SelectedTextFields is null) rule.SelectedTextFields = ["00100010"];
        RefreshSelectedTextFields();
    }

    private WatchFolderSettings? DiagnosticFolder(EncodingRule rule) =>
        Result.WatchFolders.FirstOrDefault(f => string.Equals(f.Id, rule.SourceFolderId, StringComparison.OrdinalIgnoreCase));

    private void DiagnoseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (EncodingRulesGrid.SelectedItem is not EncodingRule rule || DiagnosticFolder(rule) is not { } folder)
        {
            MessageBox.Show("Выберите правило с настроенной исходной папкой.", "Диагностика", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var files = new DicomTextDiagnosticService().AnalyzeFolderFiles(folder, rule);
            var elements = files.Where(x => x.IsApplicable).SelectMany(x => x.Elements).ToList();
            var repairable = elements.Count(x => x.Status == DicomTextElementStatus.Recoverable);
            var problems = files.Count(x => x.HasProblem);
            _lastDiagnostic = new(files.Count, files.Count, elements.Count(x => x.Status == DicomTextElementStatus.Ascii),
                elements.Count(x => x.Status == DicomTextElementStatus.Valid), repairable,
                elements.Count(x => x.Status == DicomTextElementStatus.Irrecoverable),
                elements.Count(x => x.Status == DicomTextElementStatus.Ambiguous), new Dictionary<string, int>(),
                repairable > 0 ? "Есть безопасно исправимые элементы." : "Исправимые элементы не обнаружены.", elements, repairable > 0);
            UpdateRequiredFields(elements, rule);
            ApplyRecommendationButton.IsEnabled = repairable > 0;
            LastDiagnosticText.Text = $"Последняя проверка: {files.Count} файлов • правило применяется: {files.Count(x => x.IsApplicable)} • не применяется: {files.Count(x => !x.IsApplicable)} • проблем: {problems} • можно исправить: {repairable}";
            LastDiagnosticText.Visibility = Visibility.Visible;
            System.Windows.Input.Mouse.OverrideCursor = null;
            new DicomTextDiagnosticWindow(files) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Диагностика", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
    }

    private void ApplyRecommendation_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDiagnostic?.RecommendRepair != true) return;
        if (EncodingRulesGrid.SelectedItem is not EncodingRule rule)
        {
            AddEncodingRule_Click(sender, e);
            rule = (EncodingRule)EncodingRulesGrid.SelectedItem;
        }
        rule.ProcessingMode = EncodingProcessingMode.RepairInvalidTextElements;
        rule.SourceEncodingMode = SourceEncodingMode.Automatic;
        rule.TargetEncoding = TargetDicomEncoding.IsoIr192;
        rule.RepairAllTextFields = false;
        rule.SelectedTextFields ??= ["00100010"];
        if (!rule.SelectedTextFields.Contains("00100010", StringComparer.OrdinalIgnoreCase)) rule.SelectedTextFields.Add("00100010");
        foreach (var tag in _requiredFieldTags)
            if (!rule.SelectedTextFields.Contains(tag, StringComparer.OrdinalIgnoreCase)) rule.SelectedTextFields.Add(tag);
        EncodingRulesGrid.Items.Refresh();
        EncodingFolderCombo.SelectedValue = rule.SourceFolderId;
        RepairTargetEncodingCombo.SelectedValue = rule.TargetEncoding;
        UpdateEncodingModeAvailability();
    }

    private void PreviewConversion_Click(object sender, RoutedEventArgs e)
    {
        if (EncodingRulesGrid.SelectedItem is not EncodingRule rule || DiagnosticFolder(rule) is not { } folder)
        {
            MessageBox.Show("Выберите правило и папку для проверки.", "Проверка преобразования", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var files = new DicomTextDiagnosticService().PreviewFolderFiles(folder, rule);
            var previews = files.Select(x => x.Preview).ToList();
            _requiredFieldTags.Clear();
            foreach (var tag in files.Where(x => x.IsApplicable).SelectMany(x => x.Preview.RequiredFieldTagIds ?? [])) _requiredFieldTags.Add(tag);
            ShowRequiredFields(files.Where(x => x.IsApplicable).SelectMany(x => x.Preview.Elements));
            var applicable = files.Count(x => x.IsApplicable);
            var successful = files.Count(x => x.IsApplicable && !x.HasProblem);
            LastDiagnosticText.Text = $"Последняя проверка преобразования: {previews.Count} файлов • применяется: {applicable} • не применяется: {previews.Count - applicable} • успешно: {successful} • ошибок: {files.Count(x => x.HasProblem)}";
            LastDiagnosticText.Visibility = Visibility.Visible;
            System.Windows.Input.Mouse.OverrideCursor = null;
            new DicomTextConversionWindow(files, rule.Copy()) { Owner = this }.ShowDialog();
        }
        catch (DicomEncodingException ex)
        {
            MessageBox.Show($"Преобразование небезопасно.\nТег: {ex.Tag}\n{ex.Message}\nФрагмент: {ex.ValueFragment}",
                "Проверка преобразования", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Проверка преобразования", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
    }

    private void UpdateRequiredFields(IEnumerable<DicomTextElementAnalysis> elements, EncodingRule rule)
    {
        _requiredFieldTags.Clear();
        foreach (var item in elements.Where(x => x.Status == DicomTextElementStatus.Recoverable && !RuleIncludes(rule, x.TagId)))
            _requiredFieldTags.Add(item.TagId);
        ShowRequiredFields(elements);
    }

    private void ShowRequiredFields(IEnumerable<DicomTextElementAnalysis> elements)
    {
        var items = elements.ToList();
        RequiredFieldsPanel.Visibility = _requiredFieldTags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RequiredFieldsText.Text = string.Join("\n\n", _requiredFieldTags.Select(tag =>
        {
            var item = items.FirstOrDefault(x => x.TagId.Equals(tag, StringComparison.OrdinalIgnoreCase));
            return item is null ? tag : $"{FriendlyFieldName(tag, item.FieldName)} {item.Tag}\n{item.DetectedEncoding}\n{item.RepairedValue}";
        }));
    }

    private void AddRequiredFields_Click(object sender, RoutedEventArgs e)
    {
        if (EncodingRulesGrid.SelectedItem is not EncodingRule rule) return;
        rule.RepairAllTextFields = false;
        rule.SelectedTextFields ??= [];
        foreach (var tag in _requiredFieldTags)
            if (!rule.SelectedTextFields.Contains(tag, StringComparer.OrdinalIgnoreCase)) rule.SelectedTextFields.Add(tag);
        _requiredFieldTags.Clear();
        RequiredFieldsPanel.Visibility = Visibility.Collapsed;
        RefreshSelectedTextFields();
    }

    private static bool IsRepresentative(DicomTextElementAnalysis item) => item.Tag is "(0010,0010)" or "(0010,0020)" or "(0008,0080)" or "(0018,1030)";
    private static bool RuleIncludes(EncodingRule? rule, string tagId) => rule?.RepairAllTextFields == true ||
        rule?.SelectedTextFields is null && rule?.ProcessingMode == EncodingProcessingMode.RepairInvalidTextElements ||
        (rule?.SelectedTextFields ?? ["00100010"]).Contains(tagId, StringComparer.OrdinalIgnoreCase);
    private static string FriendlyFieldName(string tagId, string fallback) => tagId switch
    {
        "00100010" => "Patient Name", "00100020" => "Patient ID", "00080080" => "Institution Name",
        "00081030" => "Study Description", "0008103E" => "Series Description", "00181030" => "Protocol Name",
        "00080090" => "Referring Physician's Name", "00081050" => "Performing Physician's Name",
        "00081060" => "Name of Physician(s) Reading Study", "00081070" => "Operators' Name", _ => fallback
    };
    private static string TargetEncodingText(TargetDicomEncoding target) => target switch
    {
        TargetDicomEncoding.IsoIr144 => "ISO_IR 144 — кириллица ISO-8859-5",
        TargetDicomEncoding.IsoIr100 => "ISO_IR 100 — Latin-1, без кириллицы",
        _ => "ISO_IR 192 — UTF-8 (рекомендуется)"
    };
    private static string StatusText(DicomTextElementStatus status) => status switch
    {
        DicomTextElementStatus.Ascii => "корректно (ASCII)",
        DicomTextElementStatus.Valid => "корректно",
        DicomTextElementStatus.Recoverable => "можно исправить",
        DicomTextElementStatus.Irrecoverable => "исходные данные повреждены; исправление невозможно",
        DicomTextElementStatus.Empty => "пустое значение",
        DicomTextElementStatus.Missing => "тег отсутствует",
        _ => "неоднозначная кодировка"
    };
    private static string DisplayValue(string? value) => string.IsNullOrEmpty(value) ? "<пусто>" : value;

    private async void Echo_Click(object sender, RoutedEventArgs e)
    {
        if (PacsGrid.SelectedItem is not PacsSettings pacs)
        {
            MessageBox.Show("Сначала выберите PACS.", "C-ECHO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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

    public event Action<PacsSettings>? RequestSendHistoricalStudies;

    private void SendHistoricalStudiesForPacs_Click(object sender, RoutedEventArgs e)
    {
        if (PacsGrid.SelectedItem is not PacsSettings pacs)
        {
            MessageBox.Show("Выберите PACS в списке слева.", "Отправка сохранённых исследований", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!pacs.Enabled)
        {
            MessageBox.Show($"PACS «{pacs.Name}» отключён. Включите PACS перед отправкой сохранённых исследований.", "Отправка сохранённых исследований", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        RequestSendHistoricalStudies?.Invoke(pacs);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyControlValues();
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ApplyControlValues()
    {
        Result.ScanIntervalSeconds = Parse(ScanIntervalText.Text, "интервал проверки");
        Result.PacsHealthCheckSeconds = Parse(PacsHealthCheckText.Text, "интервал проверки PACS");
        Result.FileStableSeconds = Parse(StableTimeText.Text, "время стабильности");
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

        foreach (var folder in Result.WatchFolders)
        {
            if (folder.EmptyFolderCleanupDelayMinutes is < 5 or > 1440)
                throw new InvalidDataException($"Для папки «{folder.Name}»: задержка удаления пустых подпапок должна быть от 5 до 1440 минут.");
        }

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

    private string CaptureState() => string.Join("|",
        JsonSerializer.Serialize(Result),
        ScanIntervalText.Text, PacsHealthCheckText.Text, StableTimeText.Text, MaxAttemptsText.Text,
        UiHoursText.Text, LogDaysText.Text, DatabaseDaysText.Text, BackupDaysText.Text,
        StartWithWindowsCheck.IsChecked, AutoStartCheck.IsChecked,
        StartMinimizedCheck.IsChecked, MinimizeToTrayCheck.IsChecked,
        DetailedLogCheck.IsChecked, LogPatientNamesCheck.IsChecked,
        DatabaseIntegrityCheck.IsChecked);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_saved && !string.Equals(_initialState, CaptureState(), StringComparison.Ordinal) &&
            MessageBox.Show(
                "Закрыть настройки без сохранения изменений?",
                "Несохранённые изменения",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void RefreshFolders(WatchFolderSettings? selected)
    {
        FoldersGrid.Items.Refresh();
        FoldersGrid.SelectedItem = selected;
        RefreshPacsChoices();
        RefreshEncodingChoices();
    }
}
