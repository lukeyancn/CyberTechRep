using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>模块 5：NoticeStore 单元测试（幂等 / 已读持久化 / 损坏恢复 / Changed 事件）。</summary>
public sealed class NoticeStoreTests : IDisposable
{
    private readonly string _dir;

    public NoticeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "notices", Guid.NewGuid().ToString("N"));
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

    private string MainFile => Path.Combine(_dir, "notices.json");

    [Fact]
    public async Task AddOrUpdate_SameMessageId_IsIdempotentSingleItem()
    {
        var store = new NoticeStore(_dir);

        await store.AddOrUpdateAsync("m1", "第一条通知");
        var updated = await store.AddOrUpdateAsync("m1", "更新后的通知");

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("更新后的通知", all[0].Content);
        Assert.Equal(updated.Id, all[0].Id);
        Assert.Equal("m1", all[0].MessageId);
    }

    [Fact]
    public async Task AddOrUpdate_SameMessageId_SameContent_DoesNotRaiseChanged()
    {
        var store = new NoticeStore(_dir);
        await store.AddOrUpdateAsync("m1", "内容");
        var eventCount = 0;
        store.Changed += (_, _) => eventCount++;

        await store.AddOrUpdateAsync("m1", "内容");

        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task MarkRead_PersistsAcrossInstances_ReadItemsDoNotRevive()
    {
        var store = new NoticeStore(_dir);
        var first = await store.AddOrUpdateAsync("m1", "A");
        await store.AddOrUpdateAsync("m2", "B");
        await store.MarkReadAsync(first.Id);

        var unread = await store.GetUnreadAsync();
        Assert.Single(unread);
        Assert.Equal("m2", unread[0].MessageId);

        // 新实例加载：已读条目不复活为未读
        var reloaded = new NoticeStore(_dir);
        var unreadAfterRestart = await reloaded.GetUnreadAsync();
        Assert.Single(unreadAfterRestart);
        Assert.DoesNotContain(unreadAfterRestart, i => i.Id == first.Id);

        var allAfterRestart = await reloaded.GetAllAsync();
        var readItem = Assert.Single(allAfterRestart, i => i.Id == first.Id);
        Assert.True(readItem.IsRead);
        Assert.NotNull(readItem.ReadAt);
    }

    [Fact]
    public async Task AddOrUpdate_AfterMarkRead_KeepsReadState()
    {
        var store = new NoticeStore(_dir);
        var item = await store.AddOrUpdateAsync("m1", "A");
        await store.MarkReadAsync(item.Id);

        var again = await store.AddOrUpdateAsync("m1", "A2");

        Assert.True(again.IsRead);
        Assert.Empty(await store.GetUnreadAsync());
    }

    [Fact]
    public async Task CorruptedJson_RestoresFromBak()
    {
        var store = new NoticeStore(_dir);
        await store.AddOrUpdateAsync("m1", "A");
        await store.AddOrUpdateAsync("m2", "B");
        Assert.True(File.Exists(MainFile));
        Assert.True(File.Exists(MainFile + ".bak"));

        File.WriteAllText(MainFile, "{ 这不是合法 JSON !!!");

        var reloaded = new NoticeStore(_dir);
        var all = await reloaded.GetAllAsync();
        Assert.Equal(2, all.Count);

        // 自愈：主文件被恢复
        Assert.True(File.Exists(MainFile));
        var recovered = await reloaded.GetUnreadAsync();
        Assert.Equal(2, recovered.Count);
    }

    [Fact]
    public async Task CorruptedJson_WithoutBak_StartsEmptyAndKeepsCorruptSnapshot()
    {
        var store = new NoticeStore(_dir);
        await store.AddOrUpdateAsync("m1", "A");

        File.WriteAllText(MainFile, "garbage");
        File.Delete(MainFile + ".bak");

        var reloaded = new NoticeStore(_dir);
        Assert.Empty(await reloaded.GetAllAsync());
        Assert.True(File.Exists(MainFile + ".corrupt"));
    }

    [Fact]
    public async Task MarkRead_UnknownId_IsNoOpWithoutSave()
    {
        var store = new NoticeStore(_dir);
        await store.AddOrUpdateAsync("m1", "A");
        var before = (await store.GetAllAsync()).Single();

        await store.MarkReadAsync(Guid.NewGuid());

        var after = (await store.GetAllAsync()).Single();
        Assert.False(after.IsRead);
        Assert.Equal(before.ReadAt, after.ReadAt);
    }

    [Fact]
    public async Task GetUnread_OrdersNewestFirst()
    {
        var store = new NoticeStore(_dir);
        var older = await store.AddOrUpdateAsync("m1", "A");
        await Task.Delay(2);
        await store.AddOrUpdateAsync("m2", "B");

        var unread = await store.GetUnreadAsync();

        Assert.Equal(2, unread.Count);
        Assert.Equal("m2", unread[0].MessageId);
        Assert.Equal(older.Id, unread[1].Id);
    }

    [Fact]
    public async Task ChangedEvent_RaisedOnAddAndMarkRead()
    {
        var store = new NoticeStore(_dir);
        var events = new List<NoticeItem>();
        store.Changed += (_, item) => events.Add(item);

        var item = await store.AddOrUpdateAsync("m1", "A");
        await store.MarkReadAsync(item.Id);

        Assert.Equal(2, events.Count);
    }
}
