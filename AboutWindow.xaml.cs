using System.Reflection;
using System.Windows;
using DicomMover.Models;
using DicomMover.Services;

namespace DicomMover;

public partial class AboutWindow : Window
{
    public AboutWindow(AppSettings settings)
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Версия {version?.ToString(2) ?? "не определена"}";
        ProgramFolderText.Text = AppPaths.BaseDirectory;
        DatabasePathText.Text = AppPaths.Resolve(settings.DatabaseFile);
        LogsFolderText.Text = Path.GetDirectoryName(AppPaths.Resolve(settings.LogFile)) ?? AppPaths.BaseDirectory;
    }
}
