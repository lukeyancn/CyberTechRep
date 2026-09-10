using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 1 学科文档存储（<see cref="HomeworkStore"/> 文档部分）单元测试：
/// 追加写入、完全重复跳过、部分重叠并集合并、手工编辑回写与增量追加、按来源消息撤回、
/// 整份文档清空、旧存档（仅有作业条目）自动重建文档。
/// </summary>
public sealed class HomeworkDocumentStoreTests : IDisposable
{
    private readonly string _dir;

    public HomeworkDocumentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "homework-doc", Guid.NewGuid().ToString("N"));
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

    private static HomeworkDocumentEntry Entry(string text, string messageId, string member = "t1", string sender = "王老师") =>
        new()
        {
            Text = text,
            MemberOpenId = member,
            SenderLabel = sender,
            SourceMessageIds = [messageId],
            CreatedAt = DateTimeOffset.Now
        };

    [Fact]
    public async Task Append_CreatesDocumentWithEntry_AndRenderReturnsText()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var document = await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));

        Assert.Equal("数学", document.Subject);
        Assert.Single(document.Entries);
        Assert.Equal("练习册 P12", document.Render);
        var loaded = await store.GetDocumentsAsync(today);
        Assert.Single(loaded);
    }

    [Fact]
    public async Task Append_ExactDuplicate_IsSkipped()
    {
        var store = new HomeworkStore(_dir);
        var first = await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));

        var second = await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m2"));

        Assert.Single(second.Entries);
        Assert.Equal(first.Entries[0].Id, second.Entries[0].Id);
        // 重复来源不并入（完全重复按跳过处理，撤回联动只针对真正写入的来源）
        Assert.Equal(["m1"], second.Entries[0].SourceMessageIds);
    }

    [Fact]
    public async Task Append_PartialOverlap_MergesAndUnionsSourceIds()
    {
        var store = new HomeworkStore(_dir);
        await store.AppendDocumentEntryAsync("数学", Entry("1. 第1页\n2. 第2页", "m1"));

        var merged = await store.AppendDocumentEntryAsync("数学", Entry("第1页\n第2页\n第3页", "m2"));

        Assert.Single(merged.Entries);
        Assert.Contains("第3页", merged.Entries[0].Text);
        Assert.Equal(["m1", "m2"], merged.Entries[0].SourceMessageIds);
    }

    [Fact]
    public async Task Append_DifferentSubjects_StaySeparate()
    {
        var store = new HomeworkStore(_dir);
        await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));
        await store.AppendDocumentEntryAsync("语文", Entry("背诵古诗", "m2"));

        var documents = await store.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, d => d.Subject == "数学");
        Assert.Contains(documents, d => d.Subject == "语文");
    }

    [Fact]
    public async Task SaveDocumentText_OverridesRender_AndEmptyTextRestoresEntries()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));

        var edited = await store.SaveDocumentTextAsync(today, "数学", "老师手改后的整篇文本");
        Assert.NotNull(edited);
        Assert.Equal("老师手改后的整篇文本", edited!.Render);
        Assert.Equal("老师手改后的整篇文本", (await store.GetDocumentsAsync(today))[0].Render);

        // 清空手工文本 → 回到按条目渲染
        var restored = await store.SaveDocumentTextAsync(today, "数学", "");
        Assert.Equal("练习册 P12", restored!.Render);
    }

    [Fact]
    public async Task Append_AfterManualEdit_AppendsNewLinesToManualText()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await store.AppendDocumentEntryAsync("数学", Entry("第1页", "m1"));
        await store.SaveDocumentTextAsync(today, "数学", "第1页");

        var document = await store.AppendDocumentEntryAsync("数学", Entry("第1页\n第2页", "m2"));

        // 手工文本保留，且新行按末尾增量追加（不覆盖用户删改）
        Assert.Equal($"第1页{Environment.NewLine}第2页", document.ManualText);
        Assert.Equal($"第1页{Environment.NewLine}第2页", document.Render);
    }

    [Fact]
    public async Task SaveDocumentText_DuringEdit_LateEntryIsMergedIntoEditorText()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var opened = await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));
        var anchor = opened.Entries.Select(e => e.Id).ToList(); // 进入编辑态时的条目锚点

        // 编辑期间新消息到达（用户还在编辑自己的稿子）
        await store.AppendDocumentEntryAsync("数学", Entry("口算 20 题", "m2", member: "t2", sender: "李老师"));

        var saved = await store.SaveDocumentTextAsync(
            today, "数学", "练习册 P12（已核对）", CancellationToken.None, anchor);

        // 用户改动保留 + 编辑期间新到的行补齐
        Assert.Equal($"练习册 P12（已核对）{Environment.NewLine}口算 20 题", saved!.Render);
    }

    [Fact]
    public async Task SaveDocumentText_DuringEdit_RewrittenOldLine_IsNotReappended()
    {
        // 回归：旧口径以「进入编辑态时的渲染文本」为基线，用户在编辑态里**改写**过的旧行
        // 既不在编辑稿里、也不在更新后的基线里，于是被当成编辑期间新到的行反复重加（文档行重复）
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var opened = await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));
        var anchor = opened.Entries.Select(e => e.Id).ToList();

        await store.SaveDocumentTextAsync(today, "数学", "练习册 P12 第 1-5 题", CancellationToken.None, anchor);
        var again = await store.SaveDocumentTextAsync(
            today, "数学", "练习册 P12 第 1-5 题（老师更正）", CancellationToken.None, anchor);

        Assert.Equal("练习册 P12 第 1-5 题（老师更正）", again!.Render);
    }

    [Fact]
    public async Task SaveDocumentText_DuringEdit_DeletedOldLine_StaysDeleted()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));
        var opened = await store.AppendDocumentEntryAsync("数学", Entry("口算 20 题", "m2", member: "t2", sender: "李老师"));
        var anchor = opened.Entries.Select(e => e.Id).ToList();

        // 用户删掉一行后落档：删除必须保留（不得被合并器补回来）
        var saved = await store.SaveDocumentTextAsync(today, "数学", "练习册 P12", CancellationToken.None, anchor);

        Assert.Equal("练习册 P12", saved!.Render);
    }

    [Fact]
    public async Task RemoveByMessageId_RemovesEntryAndItem_AndDeletesEmptyDocument()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Subject = "数学", Content = "练习册 P12" });
        await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));

        var removed = await store.RemoveByMessageIdAsync("m1");

        Assert.Equal(1, removed);
        Assert.Empty(await store.GetDocumentsAsync(today));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task RemoveByMessageId_MergedEntry_IsRemovedEntirely()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await store.AppendDocumentEntryAsync("数学", Entry("1. 第1页\n2. 第2页", "m1"));
        await store.AppendDocumentEntryAsync("数学", Entry("第1页\n第2页\n第3页", "m2"));

        // 撤回任一来源 → 合并条目整体移除（合并语义下无法只删其中一段，见交付说明的可容忍边界）
        await store.RemoveByMessageIdAsync("m1");

        Assert.Empty(await store.GetDocumentsAsync(today));
    }

    [Fact]
    public async Task RemoveDocument_RemovesDocumentAndItsItems()
    {
        var store = new HomeworkStore(_dir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await store.UpsertAsync(new HomeworkItem { MessageId = "m1", Subject = "数学", Content = "练习册 P12" });
        await store.UpsertAsync(new HomeworkItem { MessageId = "m2", Subject = "语文", Content = "背诵古诗" });
        await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));
        await store.AppendDocumentEntryAsync("语文", Entry("背诵古诗", "m2"));

        var removed = await store.RemoveDocumentAsync(today, "数学");

        Assert.Equal(1, removed);
        var documents = await store.GetDocumentsAsync(today);
        Assert.Single(documents);
        Assert.Equal("语文", documents[0].Subject);
        Assert.DoesNotContain(await store.GetAllAsync(), i => i.Subject == "数学");
    }

    [Fact]
    public async Task DocumentChanged_FiresOnAppendEditAndRemove()
    {
        var store = new HomeworkStore(_dir);
        var events = new List<HomeworkDocument>();
        store.DocumentChanged += (_, d) => events.Add(d);
        var today = DateOnly.FromDateTime(DateTime.Now);

        await store.AppendDocumentEntryAsync("数学", Entry("第1页", "m1"));
        await store.SaveDocumentTextAsync(today, "数学", "手改");
        await store.RemoveByMessageIdAsync("m1");

        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task LegacyFile_WithoutDocuments_RebuildsDocumentsFromItems()
    {
        // 向后兼容迁移：旧存档只有 items 字段
        File.WriteAllText(Path.Combine(_dir, "homework.json"), """
            {
              "items": [
                {
                  "messageId": "m1",
                  "memberOpenId": "u1",
                  "content": "练习册 P12",
                  "subject": "数学",
                  "subjectConfidence": 1.0,
                  "subjectSource": "KeywordRule",
                  "attachmentIds": [],
                  "createdAt": "2026-09-09T10:00:00+08:00"
                }
              ]
            }
            """);

        var store = new HomeworkStore(_dir);
        var documents = await store.GetDocumentsAsync(new DateOnly(2026, 9, 9));

        Assert.Single(documents);
        Assert.Equal("数学", documents[0].Subject);
        Assert.Equal("练习册 P12", documents[0].Render);
        Assert.Equal(["m1"], documents[0].Entries[0].SourceMessageIds);
    }

    [Fact]
    public async Task Documents_SurviveReload()
    {
        var store = new HomeworkStore(_dir);
        await store.AppendDocumentEntryAsync("数学", Entry("练习册 P12", "m1"));

        var reloaded = new HomeworkStore(_dir);
        var documents = await reloaded.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now));

        Assert.Single(documents);
        Assert.Equal("练习册 P12", documents[0].Render);
    }
}
