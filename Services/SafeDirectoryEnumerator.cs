namespace DicomMover.Services;

public static class SafeDirectoryEnumerator
{
    public static IEnumerable<string> EnumerateFiles(
        string rootPath,
        bool searchSubfolders,
        Action<string, Exception>? onDirectoryError = null,
        Action<string>? onDirectoryResolved = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            yield break;

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(fullRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                onDirectoryError?.Invoke(directory, ex);
                continue;
            }

            onDirectoryResolved?.Invoke(directory);

            foreach (var file in files)
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.Temporary)) != 0)
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                yield return file;
            }

            if (!searchSubfolders)
                continue;

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                onDirectoryError?.Invoke(directory, ex);
                continue;
            }

            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (string.Equals(name, "BAD", StringComparison.OrdinalIgnoreCase))
                    continue;

                pending.Push(sub);
            }
        }
    }
}
