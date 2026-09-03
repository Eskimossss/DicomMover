using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class UiLogBufferTests
{
    [Fact]
    public void AddingEntries_TrimsInBatches_WhenExceedingLimit()
    {
        var buffer = new UiLogBuffer(maxEntries: 100);

        // Adding 149 entries (limit 100 + batch 50 = triggers at 150)
        for (var i = 0; i < 149; i++)
        {
            var trimmed = buffer.Add($"Line {i}", out _);
            Assert.False(trimmed);
        }
        Assert.Equal(149, buffer.Count);

        // 150th entry triggers batch trim back down to 100
        var wasTrimmed = buffer.Add("Line 149", out _);
        Assert.True(wasTrimmed);
        Assert.Equal(100, buffer.Count);

        var snapshot = buffer.GetSnapshot();
        Assert.Equal("Line 50", snapshot[0].Line);
        Assert.Equal("Line 149", snapshot[^1].Line);
    }

    [Fact]
    public void TrimTime_DropsExpiredEntries()
    {
        var buffer = new UiLogBuffer(maxEntries: 1000);
        var oldTime = DateTime.Now.AddHours(-3);
        var recentTime = DateTime.Now.AddMinutes(-5);

        buffer.Add("Old 1", out _, oldTime);
        buffer.Add("Old 2", out _, oldTime);
        buffer.Add("Recent 1", out _, recentTime);

        Assert.Equal(3, buffer.Count);

        var changed = buffer.TrimTime(TimeSpan.FromHours(1));
        Assert.True(changed);
        Assert.Equal(1, buffer.Count);

        var remaining = buffer.GetSnapshot();
        Assert.Equal("Recent 1", remaining[0].Line);
    }

    [Fact]
    public void GetSnapshot_AppliesFilterCorrectly()
    {
        var buffer = new UiLogBuffer(maxEntries: 100);
        buffer.Add("2026-09-02 [INFO] Started", out _);
        buffer.Add("2026-09-02 [WARN] Retrying", out _);
        buffer.Add("2026-09-02 [ERROR] Failed", out _);

        Assert.Equal(3, buffer.GetSnapshot("Все").Count);
        Assert.Single(buffer.GetSnapshot("Ошибки"));
        Assert.Single(buffer.GetSnapshot("Предупреждения"));
        Assert.Single(buffer.GetSnapshot("Информация"));
        Assert.Equal("2026-09-02 [ERROR] Failed", buffer.GetSnapshot("Ошибки")[0].Line);
    }
}
