using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DicomMover.Models;
using DicomMover.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace DicomMover;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueueRow> _rows = new();
    private readonly DispatcherTimer _timer;

    private AppSettings _settings = new();
    private AppLogger? _logger;
    private Database? _database;
    private FolderMonitor? _monitor;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    public MainWindow()
    {
        InitializeComponent();

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        new DicomSetupBuilder()
            .RegisterServices(services => services.AddFellowOakDicom())
            .Build();

        LoadSettings();
        BuildServices();

        QueueGrid.ItemsSource = _rows;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => RefreshQueue();
        _timer.Start();

        RefreshQueue();
    }

    private void LoadSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (File.Exists(path))
        {
            _settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new AppSettings();
        }

        WatchFolderTextBox.Text = _settings.WatchFolder;
        RemoteHostTextBox.Text = _settings.RemoteHost;
        RemotePortTextBox.Text = _settings.RemotePort.ToString();
        RemoteAeTextBox.Text = _settings.RemoteAeTitle;
        LocalAeTextBox.Text = _settings.LocalAeTitle;
    }

    private void ReadForm()
    {
        _settings.WatchFolder = WatchFolderTextBox.Text.Trim();
        _settings.RemoteHost = RemoteHostTextBox.Text.Trim();
        _settings.RemotePort = int.Parse(RemotePortTextBox.Text);
        _settings.RemoteAeTitle = RemoteAeTextBox.Text.Trim().ToUpperInvariant();
        _settings.LocalAeTitle = LocalAeTextBox.Text.Trim().ToUpperInvariant();
    }

    private void BuildServices()
    {
        _logger = new AppLogger(_settings.LogFile);
        _logger.MessageWritten += line => Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });

        _database = new Database(_settings.DatabaseFile);
        _monitor = new FolderMonitor(_settings, _database, _logger);
    }

    private async void EchoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadForm();

            DicomStatus? responseStatus = null;

            var request = new DicomCEchoRequest
            {
                OnResponseReceived = (_, response) =>
                    responseStatus = response.Status
            };

            var client = DicomClientFactory.Create(
                _settings.RemoteHost,
                _settings.RemotePort,
                false,
                _settings.LocalAeTitle,
                _settings.RemoteAeTitle);

            await client.AddRequestAsync(request);
            await client.SendAsync(
                CancellationToken.None,
                DicomClientCancellationMode.ImmediatelyReleaseAssociation);

            var success = responseStatus?.State == DicomState.Success;

            StatusIndicator.Fill = success ? Brushes.Green : Brushes.Red;
            StatusText.Text = success ? "PACS доступен" : "PACS не ответил";
        }
        catch (Exception ex)
        {
            StatusIndicator.Fill = Brushes.Red;
            StatusText.Text = "Ошибка соединения";
            _logger?.Error($"Ошибка C-ECHO: {ex.Message}");
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTask is not null)
            return;

        try
        {
            ReadForm();
            Directory.CreateDirectory(_settings.WatchFolder);

            BuildServices();

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(
                () => _monitor!.RunAsync(_cts.Token));

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusIndicator.Fill = Brushes.Green;
            StatusText.Text = "Мониторинг активен";

            _logger?.Info("Мониторинг запущен.");

            await Task.Delay(50);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DicomMover");
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTask is null)
            return;

        _cts?.Cancel();

        try
        {
            await _monitorTask;
        }
        catch
        {
        }

        _monitorTask = null;
        _cts?.Dispose();
        _cts = null;

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusIndicator.Fill = Brushes.Gray;
        StatusText.Text = "Остановлен";

        _logger?.Info("Мониторинг остановлен.");
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();

        if (dialog.ShowDialog() == true)
            WatchFolderTextBox.Text = dialog.FolderName;
    }

    private void RefreshQueue()
    {
        if (_database is null)
            return;

        var counts = _database.GetCounts();

        PendingCountText.Text = counts.Pending.ToString();
        SentCountText.Text = counts.Sent.ToString();
        FailedCountText.Text = counts.Failed.ToString();

        _rows.Clear();

        foreach (var item in _database.GetRecent(200))
            _rows.Add(QueueRow.FromItem(item));
    }
}
