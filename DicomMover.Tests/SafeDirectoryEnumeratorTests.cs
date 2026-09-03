using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class SafeDirectoryEnumeratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SafeEnumTests-" + Guid.NewGuid().ToString("N"));

    public SafeDirectoryEnumeratorTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void EnumeratesNestedFolders_AndSkipsBadFolder()
    {
        var root = Path.Combine(_dir, "root");
        var sub1 = Path.Combine(root, "sub1");
        var sub2 = Path.Combine(root, "sub2");
        var bad = Path.Combine(root, "BAD");

        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);
        Directory.CreateDirectory(bad);

        File.WriteAllText(Path.Combine(root, "file0.dcm"), "data0");
        File.WriteAllText(Path.Combine(sub1, "file1.dcm"), "data1");
        File.WriteAllText(Path.Combine(sub2, "file2.dcm"), "data2");
        File.WriteAllText(Path.Combine(bad, "bad.dcm"), "bad_data");

        var files = SafeDirectoryEnumerator.EnumerateFiles(root, searchSubfolders: true).ToList();

        Assert.Contains(files, f => Path.GetFileName(f) == "file0.dcm");
        Assert.Contains(files, f => Path.GetFileName(f) == "file1.dcm");
        Assert.Contains(files, f => Path.GetFileName(f) == "file2.dcm");
        Assert.DoesNotContain(files, f => Path.GetFileName(f) == "bad.dcm");
    }

    [Fact]
    public void NonExistentRoot_YieldsEmpty_WithoutException()
    {
        var nonExistent = Path.Combine(_dir, "nonexistent");
        var errorCalled = false;

        var files = SafeDirectoryEnumerator.EnumerateFiles(
            nonExistent,
            searchSubfolders: true,
            onDirectoryError: (_, _) => errorCalled = true).ToList();

        Assert.Empty(files);
        Assert.True(errorCalled);
    }

    [Fact]
    public void IgnoresHiddenAndTemporaryFiles()
    {
        var root = Path.Combine(_dir, "filter_test");
        Directory.CreateDirectory(root);

        var normal = Path.Combine(root, "normal.dcm");
        var hidden = Path.Combine(root, "hidden.dcm");

        File.WriteAllText(normal, "normal");
        File.WriteAllText(hidden, "hidden");

        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var files = SafeDirectoryEnumerator.EnumerateFiles(root, searchSubfolders: false).ToList();

        Assert.Single(files);
        Assert.Equal(normal, files[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
