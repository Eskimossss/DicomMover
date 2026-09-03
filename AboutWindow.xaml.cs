using System.Reflection;
using System.Diagnostics;
using System.Windows;
using DicomMover.Models;
using DicomMover.Services;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class AboutWindow : Window
{
    private const string TelegramUrl = "https://t.me/eskimossss";
    private readonly Version _currentVersion;

    public AboutWindow(AppSettings settings)
    {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        _currentVersion = assembly.GetName().Version ?? new Version(0, 0);
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var displayVersion = !string.IsNullOrWhiteSpace(infoVersion)
            ? infoVersion.Split('+')[0].Trim()
            : _currentVersion.ToString(2);
        VersionText.Text = $"Версия {displayVersion}";
        ProgramFolderText.Text = AppPaths.BaseDirectory;
        DatabasePathText.Text = AppPaths.Resolve(settings.DatabaseFile);
        LogsFolderText.Text = Path.GetDirectoryName(AppPaths.Resolve(settings.LogFile)) ?? AppPaths.BaseDirectory;
    }

    private void TelegramLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(TelegramUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось открыть Telegram.\n\n{TelegramUrl}\n\n{ex.Message}",
                "Связь с разработчиком",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Проверка…";
        try
        {
            var release = await new GitHubUpdateChecker().GetLatestReleaseAsync();
            if (release.Version <= _currentVersion)
            {
                MessageBox.Show(
                    $"Установлена актуальная версия DicomMover {_currentVersion.ToString(2)}.",
                    "Проверка обновлений",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                $"Доступна новая версия DicomMover {release.Version.ToString(2)}.\n\nОткрыть страницу загрузки?",
                "Доступно обновление",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(release.ReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось проверить обновления.\n\n{ex.Message}",
                "Проверка обновлений",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            CheckUpdatesButton.Content = "Проверить обновления";
            CheckUpdatesButton.IsEnabled = true;
        }
    }
}
