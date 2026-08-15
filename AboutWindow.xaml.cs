using System.Reflection;
using System.Diagnostics;
using System.Windows;
using DicomMover.Models;
using DicomMover.Services;
using MessageBox = System.Windows.MessageBox;

namespace DicomMover;

public partial class AboutWindow : Window
{
    private readonly Version _currentVersion;

    public AboutWindow(AppSettings settings)
    {
        InitializeComponent();
        _currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        VersionText.Text = $"Версия {_currentVersion.ToString(2)}";
        ProgramFolderText.Text = AppPaths.BaseDirectory;
        DatabasePathText.Text = AppPaths.Resolve(settings.DatabaseFile);
        LogsFolderText.Text = Path.GetDirectoryName(AppPaths.Resolve(settings.LogFile)) ?? AppPaths.BaseDirectory;
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
