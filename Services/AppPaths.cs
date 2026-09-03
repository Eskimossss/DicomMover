namespace DicomMover.Services;

public static class AppPaths
{
    public static string BaseDirectory { get; } = GetBaseDirectory();

    public static string Resolve(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(Path.Combine(BaseDirectory, path));

    private static string GetBaseDirectory()
    {
#if DEBUG
        return FindDevelopmentRoot() ?? GetExecutableDirectory();
#else
        // Опубликованная portable-версия должна использовать каталог расположения EXE,
        // а не временный каталог распаковки single-file (AppContext.BaseDirectory).
        return GetExecutableDirectory();
#endif
    }

    private static string GetExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }
        return AppContext.BaseDirectory;
    }

#if DEBUG
    private static string? FindDevelopmentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DicomMover.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
#endif
}
