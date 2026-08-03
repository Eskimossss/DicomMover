using Microsoft.Win32;

namespace DicomMover.Services;

public static class WindowsStartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DicomMover";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
