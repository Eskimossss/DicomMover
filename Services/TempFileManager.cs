using System.Diagnostics;
using System.IO;

namespace DicomMover.Services;

public class InsufficientDiskSpaceException : IOException
{
    public long AvailableBytes { get; }
    public long RequiredBytes { get; }
    public string DriveName { get; }

    public InsufficientDiskSpaceException(string driveName, long availableBytes, long requiredBytes)
        : base($"Недостаточно свободного места на диске {driveName} для перекодирования файла. Свободно: {availableBytes / (1024 * 1024)} МБ, требуется не менее: {requiredBytes / (1024 * 1024)} МБ.")
    {
        DriveName = driveName;
        AvailableBytes = availableBytes;
        RequiredBytes = requiredBytes;
    }
}

public static class TempFileManager
{
    private static readonly string DefaultTempDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DicomMover",
        "Temp");

    public static string TempDirectory { get; set; } = DefaultTempDir;

    public static void ResetDefaultTempDirectory() => TempDirectory = DefaultTempDir;

    public static string GetTempDirectory()
    {
        if (!Directory.Exists(TempDirectory))
        {
            Directory.CreateDirectory(TempDirectory);
            SecureDirectoryForCurrentUser(TempDirectory);
        }
        return TempDirectory;
    }

    public static string CreateTempFilePath()
    {
        var dir = GetTempDirectory();
        return Path.Combine(dir, $"tx_{Environment.ProcessId}_{Guid.NewGuid():N}.dcm");
    }

    public static void EnsureSufficientDiskSpace(string targetPath, long sourceFileSize)
    {
        try
        {
            var fullPath = Path.GetFullPath(targetPath);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                // Безопасный запас: 4-кратный размер исходного файла или не менее 100 МБ
                var minRequired = Math.Max(sourceFileSize * 4, 100L * 1024 * 1024);
                if (drive.AvailableFreeSpace < minRequired)
                {
                    throw new InsufficientDiskSpaceException(drive.Name, drive.AvailableFreeSpace, minRequired);
                }
            }
        }
        catch (Exception ex) when (ex is not IOException)
        {
            // Игнорируем сбои получения информации о специфических дисках
        }
    }

    public static void SecureDirectoryForCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var dirInfo = new DirectoryInfo(path);
            var security = dirInfo.GetAccessControl();
            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.SetOwner(currentUser);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    currentUser,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                var adminSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    adminSid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            }
        }
        catch
        {
            // Не-NTFS тома или ограниченные права не должны приводить к аварийному завершению
        }
    }

    public static void CleanStaleTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempDirectory))
                return;

            var currentPid = Environment.ProcessId;
            foreach (var file in Directory.EnumerateFiles(TempDirectory, "tx_*.dcm"))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var parts = name.Split('_');
                    if (parts.Length >= 3 && int.TryParse(parts[1], out var pid))
                    {
                        if (pid == currentPid)
                            continue; // Не трогаем файлы текущего экземпляра

                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (!proc.HasExited)
                                continue; // Активный процесс другого живого экземпляра DicomMover
                        }
                        catch (ArgumentException)
                        {
                            // Процесс с таким PID уже не существует
                        }
                    }

                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (DateTime.UtcNow - lastWrite > TimeSpan.FromHours(1) || (parts.Length >= 3 && int.TryParse(parts[1], out _)))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Игнорируем заблокированные или удаляемые другими процессами файлы
                }
            }
        }
        catch
        {
            // Очистка временных файлов не должна прерывать работу программы
        }
    }
}
