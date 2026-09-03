using DicomMover.Models;
using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class DeferredFolderCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeferredCleanupTests-" + Guid.NewGuid().ToString("N"));

    public DeferredFolderCleanupTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void CleanupEmptySubfolders_DeletesEmptySubfolder_AfterDelay()
    {
        var root = Path.Combine(_dir, "watch_root");
        var sub = Path.Combine(root, "empty_old_sub");
        Directory.CreateDirectory(sub);
        Directory.SetLastWriteTimeUtc(sub, DateTime.UtcNow.AddMinutes(-35));

        var folder = new WatchFolderSettings
        {
            Id = "f1",
            Name = "Folder 1",
            Path = root,
            Enabled = true,
            SearchSubfolders = true,
            DeleteEmptySubfolders = true,
            EmptyFolderCleanupDelayMinutes = 30,
            PostSendAction = PostSendAction.Delete
        };

        var settings = new AppSettings { WatchFolders = [folder] };
        var monitor = new FolderMonitor(settings, new Database(Path.Combine(_dir, "test.db")), new AppLogger(Path.Combine(_dir, "app.log")));

        monitor.CleanupEmptySubfolders(folder);

        Assert.False(Directory.Exists(sub), "Subfolder older than delay must be deleted.");
        Assert.True(Directory.Exists(root), "Root folder must never be deleted.");
    }

    [Fact]
    public void CleanupEmptySubfolders_DoesNotDeleteSubfolder_BeforeDelay()
    {
        var root = Path.Combine(_dir, "watch_root_young");
        var sub = Path.Combine(root, "empty_young_sub");
        Directory.CreateDirectory(sub);
        Directory.SetLastWriteTimeUtc(sub, DateTime.UtcNow.AddMinutes(-10));

        var folder = new WatchFolderSettings
        {
            Id = "f1",
            Name = "Folder 1",
            Path = root,
            Enabled = true,
            SearchSubfolders = true,
            DeleteEmptySubfolders = true,
            EmptyFolderCleanupDelayMinutes = 30,
            PostSendAction = PostSendAction.Delete
        };

        var settings = new AppSettings { WatchFolders = [folder] };
        var monitor = new FolderMonitor(settings, new Database(Path.Combine(_dir, "test.db")), new AppLogger(Path.Combine(_dir, "app.log")));

        monitor.CleanupEmptySubfolders(folder);

        Assert.True(Directory.Exists(sub), "Subfolder younger than delay must NOT be deleted.");
    }

    [Fact]
    public void CleanupEmptySubfolders_DoesNotDeleteSubfolder_IfNotEmpty()
    {
        var root = Path.Combine(_dir, "watch_root_not_empty");
        var sub = Path.Combine(root, "non_empty_sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "data.dcm"), "some data");
        Directory.SetLastWriteTimeUtc(sub, DateTime.UtcNow.AddMinutes(-40));

        var folder = new WatchFolderSettings
        {
            Id = "f1",
            Name = "Folder 1",
            Path = root,
            Enabled = true,
            SearchSubfolders = true,
            DeleteEmptySubfolders = true,
            EmptyFolderCleanupDelayMinutes = 30,
            PostSendAction = PostSendAction.Delete
        };

        var settings = new AppSettings { WatchFolders = [folder] };
        var monitor = new FolderMonitor(settings, new Database(Path.Combine(_dir, "test.db")), new AppLogger(Path.Combine(_dir, "app.log")));

        monitor.CleanupEmptySubfolders(folder);

        Assert.True(Directory.Exists(sub), "Non-empty subfolder must NOT be deleted.");
    }

    [Fact]
    public void CleanupEmptySubfolders_DoesNotDeleteBadFolder()
    {
        var root = Path.Combine(_dir, "watch_root_bad");
        var bad = Path.Combine(root, "BAD");
        Directory.CreateDirectory(bad);
        Directory.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddMinutes(-60));

        var folder = new WatchFolderSettings
        {
            Id = "f1",
            Name = "Folder 1",
            Path = root,
            Enabled = true,
            SearchSubfolders = true,
            DeleteEmptySubfolders = true,
            EmptyFolderCleanupDelayMinutes = 30,
            PostSendAction = PostSendAction.Delete
        };

        var settings = new AppSettings { WatchFolders = [folder] };
        var monitor = new FolderMonitor(settings, new Database(Path.Combine(_dir, "test.db")), new AppLogger(Path.Combine(_dir, "app.log")));

        monitor.CleanupEmptySubfolders(folder);

        Assert.True(Directory.Exists(bad), "BAD folder must never be deleted by empty cleanup.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
