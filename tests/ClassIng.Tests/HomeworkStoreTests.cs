using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>模块 6：HomeworkStore 单元测试（Upsert 幂等 / SetSubject 写回 / 损坏恢复 / Changed 事件）。</summary>
public sealed class HomeworkStoreTests : IDisposable
{
    private readonly string _dir;

    public HomeworkStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "homework", Guid.NewGuid().ToString("N"));
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

    private string MainFile => Path.Combine(_dir, "homework.json");

    [Fact]
    public async Task Upsert_SameMessageId_IsIdempotentSingleItem()
    {
        var store = new HomeworkStore(_dir);

        await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Subject = "数学", Content = "第1页" });
        var merged = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            Subject = "语文",
            Content = "第2页"
        });

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("语文", all[0].Subject);
        Assert.Equal("第2页", all[0].Content);
        Assert.Equal(merged.Id, all[0].Id);
        Assert.Equal("m1", all[0].MessageId);
    }

    [Fact]
    public async Task Upsert_SameId_UpdatesInPlace()
    {
        var store = new HomeworkStore(_dir);
        var first = await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Content = "v1" });

        await store.UpsertAsync(new HomeworkItem
        {
            Id = first.Id,
            MessageId = "m1",
            Subject = "英语",
            Content = "v2"
        });

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("英语", all[0].Subject);
        Assert.Equal("v2", all[0].Content);
        Assert.Equal(first.CreatedAt, all[0].CreatedAt);
    }

    [Fact]
    public async Task SetSubject_WritesBackWithManualSourceAndPersists()
    {
        var store = new HomeworkStore(_dir);
        var item = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            Subject = "未分类",
            SubjectConfidence = 0.3,
            Content = "练习题"
        });

        await store.SetSubjectAsync(item.Id, "物理");

        var matched = await store.GetBySubjectAsync("物理");
        var updated = Assert.Single(matched);
        Assert.Equal(item.Id, updated.Id);
        Assert.Equal("物理", updated.Subject);
        Assert.Equal(SubjectSource.Manual, updated.SubjectSource);
        Assert.Equal(1.0, updated.SubjectConfidence);

        // 跨实例：写回已持久化
        var reloaded = await new HomeworkStore(_dir).GetBySubjectAsync("物理");
        Assert.Single(reloaded);
    }

    [Fact]
    public async Task SetSubject_UnknownId_IsNoOp()
    {
        var store = new HomeworkStore(_dir);
        await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Subject = "数学", Content = "c" });

        await store.SetSubjectAsync(Guid.NewGuid(), "化学");

        Assert.Single(await store.GetBySubjectAsync("数学"));
        Assert.Empty(await store.GetBySubjectAsync("化学"));
    }

    [Fact]
    public async Task SetSubject_MovesItemBetweenSubjectGroups()
    {
        var store = new HomeworkStore(_dir);
        var item = await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Subject = "数学", Content = "c" });

        await store.SetSubjectAsync(item.Id, "化学");

        Assert.Empty(await store.GetBySubjectAsync("数学"));
        Assert.Single(await store.GetBySubjectAsync("化学"));
    }

    [Fact]
    public async Task CorruptedJson_RestoresFromBak()
    {
        var store = new HomeworkStore(_dir);
        await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Subject = "数学", Content = "A" });
        await store.UpsertAsync(new HomeworkItem { MessageId = "m2", Subject = "英语", Content = "B" });
        Assert.True(File.Exists(MainFile + ".bak"));

        File.WriteAllText(MainFile, "]}corrupted{");

        var reloaded = new HomeworkStore(_dir);
        var all = await reloaded.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, i => i.Subject == "数学");
        Assert.Contains(all, i => i.Subject == "英语");
    }

    [Fact]
    public async Task CorruptedJson_WithoutBak_StartsEmptyAndKeepsCorruptSnapshot()
    {
        var store = new HomeworkStore(_dir);
        await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Content = "A" });

        File.WriteAllText(MainFile, "garbage");
        File.Delete(MainFile + ".bak");

        var reloaded = new HomeworkStore(_dir);
        Assert.Empty(await reloaded.GetAllAsync());
        Assert.True(File.Exists(MainFile + ".corrupt"));
    }

    [Fact]
    public async Task ChangedEvent_RaisedOnUpsertAndSetSubject()
    {
        var store = new HomeworkStore(_dir);
        var events = new List<HomeworkItem>();
        store.Changed += (_, item) => events.Add(item);

        var item = await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Content = "A" });
        await store.SetSubjectAsync(item.Id, "数学");

        Assert.Equal(2, events.Count);
    }
}
