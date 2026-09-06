using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>
/// 存储层集成测试：学科优先级矩阵接入 HomeworkStore、通知学科前缀写入时生效、
/// 按天归桶查询（GetByDateAsync）与保留期清理（CleanupAsync）。
/// </summary>
public sealed class StoreMatrixAndRetentionTests : IDisposable
{
    private readonly string _dir;

    public StoreMatrixAndRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "matrix-retention", Guid.NewGuid().ToString("N"));
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

    // ============ ① 学科优先级矩阵（HomeworkStore 集成） ============

    [Fact]
    public async Task NewHomework_ChainHitWithExistingRule_KeepsChainSubject()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new HomeworkStore(_dir, userRules: rules);
        var item = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u1",
            Subject = "数学",
            SubjectConfidence = 0.95,
            SubjectSource = SubjectSource.KeywordRule,
            Content = "口算一页"
        });

        // 识别链正常命中 → 不被映射覆盖（修复前：被强制改为「英语」且标记 Manual）
        Assert.Equal("数学", item.Subject);
        Assert.Equal(SubjectSource.KeywordRule, item.SubjectSource);
    }

    [Fact]
    public async Task NewHomework_ChainUnclassifiedWithRule_AppliesRuleAsManual()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new HomeworkStore(_dir, userRules: rules);
        var item = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u1",
            Subject = "未分类",
            SubjectConfidence = 0.2,
            SubjectSource = SubjectSource.Manual,
            Content = "内容"
        });

        Assert.Equal("英语", item.Subject);
        Assert.Equal(SubjectSource.Manual, item.SubjectSource);
        Assert.Equal(1.0, item.SubjectConfidence);
    }

    [Fact]
    public async Task NewHomework_WithoutRule_KeepsChainResult()
    {
        var store = new HomeworkStore(_dir);
        var item = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u1",
            Subject = "物理",
            SubjectConfidence = 0.9,
            SubjectSource = SubjectSource.LocalModel,
            Content = "c"
        });

        Assert.Equal("物理", item.Subject);
        Assert.Equal(SubjectSource.LocalModel, item.SubjectSource);
    }

    [Fact]
    public async Task Merge_ManualEntry_NeverRegressesOnReplay()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new HomeworkStore(_dir, userRules: rules);
        var manual = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u1",
            Subject = "化学",
            SubjectConfidence = 1.0,
            SubjectSource = SubjectSource.Manual,
            Content = "v1"
        });

        // 协议端重投（识别链结果）：人工修正永不回退
        var merged = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u1",
            Subject = "未分类",
            SubjectConfidence = 0.1,
            SubjectSource = SubjectSource.Manual,
            Content = "v2"
        });

        Assert.Equal(manual.Id, merged.Id);
        Assert.Equal("化学", merged.Subject);
        Assert.Equal(SubjectSource.Manual, merged.SubjectSource);
    }

    [Fact]
    public async Task LearningGate_SetSubjectOnUnclassified_LearnsRuleForFutureUnclassifiedOnly()
    {
        var store = new HomeworkStore(_dir);

        // 历史：一条识别链命中的数学 + 一条未分类
        var chainItem = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u1",
            Subject = "数学",
            SubjectConfidence = 0.95,
            SubjectSource = SubjectSource.KeywordRule,
            Content = "识别成功"
        });
        var unclassified = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m2",
            MemberOpenId = "u1",
            Subject = "未分类",
            SubjectConfidence = 0.1,
            SubjectSource = SubjectSource.Manual,
            Content = "识别失败"
        });

        // 人工修正未分类作业 → 学习映射 + 同步历史（识别链命中的历史不覆盖）
        await store.SetSubjectAsync(unclassified.Id, "英语");

        var all = await store.GetAllAsync();
        Assert.Equal("数学", all.Single(i => i.Id == chainItem.Id).Subject); // 链命中历史不被覆盖
        Assert.Equal("英语", all.Single(i => i.Id == unclassified.Id).Subject);

        // 后续：识别链命中 → 仍按链结果；识别链未分类 → 套用映射
        var laterChain = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m3",
            MemberOpenId = "u1",
            Subject = "物理",
            SubjectConfidence = 0.9,
            SubjectSource = SubjectSource.CloudLlm,
            Content = "c3"
        });
        var laterUnclassified = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m4",
            MemberOpenId = "u1",
            Subject = "未分类",
            SubjectConfidence = 0.1,
            SubjectSource = SubjectSource.Manual,
            Content = "c4"
        });

        Assert.Equal("物理", laterChain.Subject);
        Assert.Equal("英语", laterUnclassified.Subject);
    }

    [Fact]
    public async Task UserSubjectsFile_ExistingData_IsLoadedLossless()
    {
        // 既有 user-subjects.json 数据无损兼容（旧格式字段不变）
        var json = """
            {
              "rules": [
                { "memberOpenId": "u-legacy", "subject": "语文", "updatedAt": "2026-01-01T00:00:00+08:00" }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(_dir, "user-subjects.json"), json);

        var store = new HomeworkStore(_dir);
        var item = await store.UpsertAsync(new HomeworkItem
        {
            MessageId = "m1",
            MemberOpenId = "u-legacy",
            Subject = "未分类",
            SubjectConfidence = 0.1,
            SubjectSource = SubjectSource.Manual,
            Content = "c"
        });

        Assert.Equal("语文", item.Subject);
    }

    // ============ ② 通知学科前缀（NoticeStore 写入时生效） ============

    [Fact]
    public async Task Notice_WithMappedSender_IsPrefixedAtWriteTime()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new NoticeStore(_dir, userRules: rules);
        var item = await store.AddOrUpdateAsync("m1", "明天交作业", memberOpenId: "u1");

        Assert.Equal("英语：明天交作业", item.Content);

        // 持久化：重启后内容仍带前缀
        var reloaded = await new NoticeStore(_dir, userRules: rules).GetAllAsync();
        Assert.Equal("英语：明天交作业", reloaded[0].Content);
    }

    [Fact]
    public async Task Notice_WithoutMapping_IsNotPrefixed()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new NoticeStore(_dir, userRules: rules);
        var item = await store.AddOrUpdateAsync("m1", "系统公告", memberOpenId: "u-other");

        Assert.Equal("系统公告", item.Content);
    }

    [Fact]
    public async Task Notice_PrefixDisabledBySetting_IsNotPrefixed()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new NoticeStore(_dir, userRules: rules, noticePrefixEnabled: () => false);
        var item = await store.AddOrUpdateAsync("m1", "明天交作业", memberOpenId: "u1");

        Assert.Equal("明天交作业", item.Content);
    }

    [Fact]
    public async Task Notice_ReplaySameMessageId_NoDoublePrefix()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new NoticeStore(_dir, userRules: rules);
        await store.AddOrUpdateAsync("m1", "明天交作业", memberOpenId: "u1");
        var again = await store.AddOrUpdateAsync("m1", "明天交作业", memberOpenId: "u1");

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("英语：明天交作业", again.Content);
    }

    [Fact]
    public async Task Notice_UpdateNoticeWithoutMember_IsNeverPrefixed()
    {
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");

        var store = new NoticeStore(_dir, userRules: rules);
        var item = await store.AddOrUpdateAsync("update:1.2.3", "发现新版本 v1.2.3");

        Assert.Equal("发现新版本 v1.2.3", item.Content);
    }

    // ============ ③ 按天归桶 + 保留期清理 ============

    /// <summary>直接写入 notices.json（CamelCase，与 JsonStoreFile 序列化一致），精确控制 CreatedAt。</summary>
    private void SeedNotices(params (string MessageId, string Content, string CreatedAt, bool IsRead)[] items)
    {
        var entries = string.Join(",\n", items.Select(i => $$"""
            { "id": "{{Guid.NewGuid()}}", "messageId": "{{i.MessageId}}", "content": "{{i.Content}}", "createdAt": "{{i.CreatedAt}}", "isRead": {{(i.IsRead ? "true" : "false").ToLowerInvariant()}}, "readAt": null }
            """));
        File.WriteAllText(
            Path.Combine(_dir, "notices.json"),
            $"{{\n  \"items\": [\n{entries}\n  ]\n}}");
    }

    private void SeedHomework(params (string MessageId, string Subject, string CreatedAt)[] items)
    {
        var entries = string.Join(",\n", items.Select(i => $$"""
            { "id": "{{Guid.NewGuid()}}", "messageId": "{{i.MessageId}}", "memberOpenId": "", "subject": "{{i.Subject}}", "subjectConfidence": 1.0, "subjectSource": "Manual", "content": "c", "attachmentIds": [], "createdAt": "{{i.CreatedAt}}", "isResolved": false }
            """));
        File.WriteAllText(
            Path.Combine(_dir, "homework.json"),
            $"{{\n  \"items\": [\n{entries}\n  ]\n}}");
    }

    private static string Iso(DateOnly day) => $"{day:yyyy-MM-dd}T08:30:00+08:00";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public async Task Notice_GetByDate_ReturnsOnlyThatDayBucket()
    {
        SeedNotices(
            (MessageId: "today", Content: "当天", CreatedAt: Iso(Today), IsRead: false),
            (MessageId: "yesterday", Content: "昨天", CreatedAt: Iso(Today.AddDays(-1)), IsRead: false));

        var store = new NoticeStore(_dir);
        var todayItems = await store.GetByDateAsync(Today);

        var item = Assert.Single(todayItems);
        Assert.Equal("today", item.MessageId);
    }

    [Fact]
    public async Task Notice_Cleanup_RemovesExpiredReadBuckets_KeepsUnreadAndToday()
    {
        SeedNotices(
            (MessageId: "read-today", Content: "当天已读", CreatedAt: Iso(Today), IsRead: true),
            (MessageId: "read-old", Content: "过期已读", CreatedAt: Iso(Today.AddDays(-3)), IsRead: true),
            (MessageId: "unread-old", Content: "过期未读", CreatedAt: Iso(Today.AddDays(-30)), IsRead: false));

        var store = new NoticeStore(_dir);
        var events = new List<NoticeItem>();
        store.Changed += (_, i) => events.Add(i);

        var removed = await store.CleanupAsync();

        Assert.Equal(1, removed);
        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, i => i.MessageId == "read-old");
        Assert.Contains(all, i => i.MessageId == "unread-old");   // 未读绝不动
        Assert.Contains(all, i => i.MessageId == "read-today");   // 当天不动
        Assert.Single(events);                                    // 删除条目触发 Changed
        Assert.Equal("read-old", events[0].MessageId);

        // 持久化：重启后不复活
        var reloaded = await new NoticeStore(_dir).GetAllAsync();
        Assert.Equal(2, reloaded.Count);
    }

    [Fact]
    public async Task Notice_Cleanup_RetentionZero_IsNoOp()
    {
        SeedNotices(
            (MessageId: "read-old", Content: "过期已读", CreatedAt: Iso(Today.AddDays(-3)), IsRead: true),
            (MessageId: "read-ancient", Content: "远古已读", CreatedAt: Iso(new DateOnly(2020, 1, 1)), IsRead: true));

        var store = new NoticeStore(_dir, noticesRetentionDays: () => 0);
        var removed = await store.CleanupAsync();

        Assert.Equal(0, removed);
        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Homework_GetByDate_SupportsTodayOnlyFiltering()
    {
        SeedHomework(
            (MessageId: "today", Subject: "数学", CreatedAt: Iso(Today)),
            (MessageId: "yesterday", Subject: "英语", CreatedAt: Iso(Today.AddDays(-1))));

        var store = new HomeworkStore(_dir);
        var todayItems = await store.GetByDateAsync(Today);

        var item = Assert.Single(todayItems);
        Assert.Equal("today", item.MessageId);
        Assert.Equal("数学", item.Subject);
    }

    [Fact]
    public async Task Homework_Cleanup_RemovesBucketsOlderThanRetention()
    {
        SeedHomework(
            (MessageId: "today", Subject: "数学", CreatedAt: Iso(Today)),
            (MessageId: "d-1", Subject: "数学", CreatedAt: Iso(Today.AddDays(-1))),
            (MessageId: "d-2", Subject: "数学", CreatedAt: Iso(Today.AddDays(-2))),
            (MessageId: "d-3", Subject: "数学", CreatedAt: Iso(Today.AddDays(-3))));

        var store = new HomeworkStore(_dir, homeworkRetentionDays: () => 2);
        var events = new List<HomeworkItem>();
        store.Changed += (_, i) => events.Add(i);

        var removed = await store.CleanupAsync();

        // 保留最近 2 天（含当天）：today 与 d-1；d-2/d-3 删除
        Assert.Equal(2, removed);
        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, i => i.MessageId == "today");
        Assert.Contains(all, i => i.MessageId == "d-1");
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Homework_Cleanup_RetentionZero_IsNoOp()
    {
        SeedHomework((MessageId: "ancient", Subject: "数学", CreatedAt: Iso(new DateOnly(2020, 1, 1))));

        var store = new HomeworkStore(_dir);
        var removed = await store.CleanupAsync();

        Assert.Equal(0, removed);
        Assert.Single(await store.GetAllAsync());
    }
}
