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
        return FindDevelopmentRoot() ?? AppContext.BaseDirectory;
#else
        // Опубликованная portable-версия не должна искать настройки в родительских
        // каталогах, даже если её распаковали внутри папки с исходным проектом.
        return AppContext.BaseDirectory;
#endif
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
