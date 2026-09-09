using CyberTechRep.Plugin.Services.MessageAccess;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// NapCatResumeCursorStore 单元测试（需求 4：断点续传游标的覆盖判定 / 单调推进 /
/// 环形有界 / 原子落盘与重启加载 / 损坏自愈 / 群容量淘汰）。
/// </summary>
public sealed class NapCatResumeCursorStoreTests : IDisposable
{
    private readonly string _dir;

    public NapCatResumeCursorStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "resume-cursor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private string MainFile => Path.Combine(_dir, "napcat-resume-cursor.json");

    [Fact]
    public void Record_ThenIsCovered_ByMessageId()
    {
        using var store = new NapCatResumeCursorStore(_dir);

        store.Record("g1", "m1", 1000, NapCatResumeCursorStore.ContentHashKey(1000, "hello"));

        Assert.True(store.IsCovered("g1", "m1", 0));
        var entry = Assert.IsType<NapCatResumeCursorEntry>(store.Get("g1"));
        Assert.Equal("m1", entry.LastMessageId);
        Assert.Equal(1000, entry.LastTimestampUnix);
    }

    [Fact]
    public void IsCovered_ByRecentMessageId()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        for (var i = 0; i < 5; i++)
        {
            store.Record("g1", $"m{i}", 1000 + i, NapCatResumeCursorStore.ContentHashKey(1000 + i, $"内容{i}"));
        }

        // m2 不是末条 id，但仍在最近 id 环形里
        Assert.True(store.IsCovered("g1", "m2", 0));
    }

    [Fact]
    public void IsCovered_OlderTimestamp_IsCovered()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        store.Record("g1", "m5", 5000);

        Assert.True(store.IsCovered("g1", "older-id", 4999));
        Assert.False(store.IsCovered("g1", "newer-id", 5001));
        Assert.False(store.IsCovered("g1", "same-second-no-hash", 5000));
    }

    [Fact]
    public void IsCovered_SameSecond_RequiresMatchingContentHash()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        store.Record("g1", "m1", 1000, NapCatResumeCursorStore.ContentHashKey(1000, "同一秒内容"));

        Assert.True(store.IsCovered("g1", "other-id", 1000,
            NapCatResumeCursorStore.ContentHashKey(1000, "同一秒内容")));
        Assert.False(store.IsCovered("g1", "other-id", 1000,
            NapCatResumeCursorStore.ContentHashKey(1000, "另一条内容")));
    }

    [Fact]
    public void IsCovered_UnknownOrEmptyGroup_IsNotCovered()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        store.Record("g1", "m1", 1000, NapCatResumeCursorStore.ContentHashKey(1000, "hello"));

        Assert.False(store.IsCovered("g2", "m1", 1000, NapCatResumeCursorStore.ContentHashKey(1000, "hello")));
        Assert.False(store.IsCovered("", "m1", 1000));
        Assert.Null(store.Get("g2"));
    }

    [Fact]
    public void Record_OlderTimestamp_DoesNotMoveCursorBack()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        store.Record("g1", "m2", 2000, NapCatResumeCursorStore.ContentHashKey(2000, "second"));
        store.Record("g1", "m1", 1000, NapCatResumeCursorStore.ContentHashKey(1000, "first"));

        var entry = Assert.IsType<NapCatResumeCursorEntry>(store.Get("g1"));
        Assert.Equal(2000, entry.LastTimestampUnix);
        Assert.Equal("m2", entry.LastMessageId);
        Assert.DoesNotContain("m1", entry.RecentMessageIds);
        Assert.DoesNotContain(entry.RecentKeys, k => k.StartsWith("1000:", StringComparison.Ordinal));
    }

    [Fact]
    public void RecentRings_AreBoundedAtCapacity()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        var total = NapCatResumeCursorStore.RecentKeyCapacity + 20;
        for (var i = 0; i < total; i++)
        {
            store.Record("g1", $"m{i}", 1000 + i, NapCatResumeCursorStore.ContentHashKey(1000 + i, $"内容{i}"));
        }

        var entry = Assert.IsType<NapCatResumeCursorEntry>(store.Get("g1"));
        Assert.Equal(NapCatResumeCursorStore.RecentKeyCapacity, entry.RecentKeys.Count);
        Assert.Equal(NapCatResumeCursorStore.RecentKeyCapacity, entry.RecentMessageIds.Count);
        Assert.DoesNotContain("m0", entry.RecentMessageIds);
        Assert.Contains($"m{total - 1}", entry.RecentMessageIds);
    }

    [Fact]
    public async Task FlushAsync_ThenReload_PreservesEntries()
    {
        using (var store = new NapCatResumeCursorStore(_dir))
        {
            store.Record("g1", "m1", 1000, NapCatResumeCursorStore.ContentHashKey(1000, "a"));
            store.Record("g2", "m2", 2000, NapCatResumeCursorStore.ContentHashKey(2000, "b"));
            await store.FlushAsync();
        }

        Assert.True(File.Exists(MainFile));

        using var reloaded = new NapCatResumeCursorStore(_dir);
        Assert.Equal(2, reloaded.Count);
        var entry = Assert.IsType<NapCatResumeCursorEntry>(reloaded.Get("g1"));
        Assert.Equal("m1", entry.LastMessageId);
        Assert.Equal(1000, entry.LastTimestampUnix);
        Assert.True(reloaded.IsCovered("g1", "m1", 0));
        Assert.True(reloaded.IsCovered("g2", "", 2000, NapCatResumeCursorStore.ContentHashKey(2000, "b")));
    }

    [Fact]
    public void CorruptedJson_StartsEmptyWithoutThrowing()
    {
        File.WriteAllText(MainFile, "]} not json {");

        using var store = new NapCatResumeCursorStore(_dir);

        Assert.Equal(0, store.Count);
        Assert.False(store.IsCovered("g1", "m1", 1000));
        Assert.True(File.Exists(MainFile + ".corrupt"));
    }

    [Fact]
    public void MaxGroups_TrimsLeastRecentlyUpdated()
    {
        using var store = new NapCatResumeCursorStore(_dir);
        store.Record("g0", "m", 1);

        // 确保 g0 的 UpdatedAt 严格早于后续群，让「最久未更新」排序确定
        Thread.Sleep(50);
        for (var i = 1; i <= NapCatResumeCursorStore.MaxGroups; i++)
        {
            store.Record($"g{i}", "m", 1000 + i);
        }

        Assert.Equal(NapCatResumeCursorStore.MaxGroups, store.Count);
        Assert.Null(store.Get("g0"));
        Assert.NotNull(store.Get($"g{NapCatResumeCursorStore.MaxGroups}"));
    }

    [Fact]
    public void ContentHashKey_IsDeterministic_AndDistinguishesContent()
    {
        var a = NapCatResumeCursorStore.ContentHashKey(1700000000, "内容A");
        var b = NapCatResumeCursorStore.ContentHashKey(1700000000, "内容A");
        var c = NapCatResumeCursorStore.ContentHashKey(1700000000, "内容B");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("1700000000:", a);

        // 时间缺失（0）时只用哈希，不带时间前缀；null 内容按空串处理
        var hashOnly = NapCatResumeCursorStore.ContentHashKey(0, "x");
        Assert.DoesNotContain(":", hashOnly);
        Assert.Equal(hashOnly, NapCatResumeCursorStore.ContentHashKey(0, "x"));
        Assert.Equal(
            NapCatResumeCursorStore.ContentHashKey(0, ""),
            NapCatResumeCursorStore.ContentHashKey(0, null));
    }
}
