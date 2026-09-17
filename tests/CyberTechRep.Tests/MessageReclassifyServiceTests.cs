using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// MessageReclassifyService 单元测试（需求 5：通知 ↔ 作业互换 / 整份文档迁移 /
/// 换类覆盖持久化 / 不存在的 id 安全返回）。
/// </summary>
public sealed class MessageReclassifyServiceTests : IDisposable
{
    private readonly string _dir;

    public MessageReclassifyServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "reclassify", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task MoveNoticeToHomeworkAsync_MigratesContentMetadataAndRecordsOverride()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var notice = await notices.AddOrUpdateAsync("m1", "数学作业第 3 页", "member-1");

        var moved = await service.MoveNoticeToHomeworkAsync(notice.Id, "数学");

        Assert.True(moved);
        Assert.Empty(await notices.GetAllAsync());

        var item = Assert.Single(await homework.GetBySubjectAsync("数学"));
        Assert.Equal("m1", item.MessageId);
        Assert.Equal("member-1", item.MemberOpenId);
        Assert.Equal("数学作业第 3 页", item.Content);
        Assert.Equal(notice.CreatedAt, item.CreatedAt);
        Assert.Equal(SubjectSource.Manual, item.SubjectSource);
        Assert.Equal(1.0, item.SubjectConfidence);

        // 学科文档同步追加来源条目
        var document = Assert.Single(await homework.GetDocumentsAsync(RetentionPolicies.BucketOf(notice.CreatedAt)));
        Assert.Equal("数学", document.Subject);
        var entry = Assert.Single(document.Entries);
        Assert.Equal("数学作业第 3 页", entry.Text);
        Assert.Contains("m1", entry.SourceMessageIds);

        Assert.True(service.TryGetKindOverride("m1", out var kind));
        Assert.Equal(MessageKind.Homework, kind);
    }

    [Fact]
    public async Task MoveNoticeToHomeworkAsync_EmptySubject_FallsBackToUnclassified()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var notice = await notices.AddOrUpdateAsync("m1b", "未指定学科的内容");

        Assert.True(await service.MoveNoticeToHomeworkAsync(notice.Id, "   "));

        var item = Assert.Single(await homework.GetBySubjectAsync(HomeworkSubjectResolver.Unclassified));
        Assert.Equal("未指定学科的内容", item.Content);
    }

    [Fact]
    public async Task MoveHomeworkToNoticeAsync_MigratesContentAndRemovesDocumentEntry()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var created = DateTimeOffset.Now.AddDays(-3);
        var item = await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "m2",
            Content = "语文背诵第 2 段",
            MemberOpenId = "member-2",
            Subject = "语文",
            CreatedAt = created
        });
        var date = RetentionPolicies.BucketOf(item.CreatedAt);
        await homework.AppendDocumentEntryAsync("语文", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m2"],
            MemberOpenId = "member-2",
            SenderLabel = "张老师",
            Text = item.Content,
            CreatedAt = item.CreatedAt
        });

        var moved = await service.MoveHomeworkToNoticeAsync(item.Id);

        Assert.True(moved);
        Assert.Empty(await homework.GetAllAsync());
        Assert.Empty(await homework.GetDocumentsAsync(date));

        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("m2", notice.MessageId);
        // 需求 2：学科标签随换类继承 → 通知正文带「语文：」前缀（旧行为不加前缀）
        Assert.Equal("语文：语文背诵第 2 段", notice.Content);
        Assert.Equal("语文", notice.Subject);
        // 需求 2：来源展示名从作业文档条目继承（通知界面不显示，仅供再次换类带回作业侧）
        Assert.Equal("张老师", notice.SenderLabel);
        Assert.Equal("member-2", notice.MemberOpenId);
        // 需求 5：时间元信息随内容一并迁移（不得改写为「换类发生时刻」）
        Assert.Equal(created, notice.CreatedAt);

        Assert.True(service.TryGetKindOverride("m2", out var kind));
        Assert.Equal(MessageKind.Notice, kind);
    }

    [Fact]
    public async Task MoveDocumentToNoticeAsync_MigratesEachEntryAndRecordsOverrides()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        // 固定到「两天前的本地上午 9:00」：不能用 Now.AddDays(-2)+AddMinutes(5)——
        // 在深夜（如 23:55）运行时会跨过午夜，两条条目落到不同归档日，迁移数变 1（时间陷阱）
        var localNow = DateTimeOffset.Now;
        var created = new DateTimeOffset(localNow.Date.AddDays(-2).AddHours(9), localNow.Offset);
        var later = created.AddMinutes(5);
        var date = RetentionPolicies.BucketOf(created);
        await homework.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m3"],
            MemberOpenId = "member-3",
            Text = "第 3 页练习题",
            CreatedAt = created
        });
        await homework.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m4"],
            MemberOpenId = "member-4",
            Text = "第 4 页练习题",
            CreatedAt = later
        });

        var migrated = await service.MoveDocumentToNoticeAsync(date, "数学");

        Assert.Equal(2, migrated);
        Assert.Empty(await homework.GetDocumentsAsync(date));

        var all = await notices.GetAllAsync();
        Assert.Equal(2, all.Count);
        // 需求 2：每条通知继承文档学科标签（落档 Subject 与「数学：」前缀）
        Assert.Contains(all, n => n.MessageId == "m3" && n.Content == "数学：第 3 页练习题"
            && n.Subject == "数学" && n.MemberOpenId == "member-3");
        Assert.Contains(all, n => n.MessageId == "m4" && n.Content == "数学：第 4 页练习题"
            && n.Subject == "数学" && n.MemberOpenId == "member-4");
        // 需求 5：每条来源消息的原始时间随内容迁移（逐条对应，不是统一的换类时刻）
        Assert.Equal(created, all.Single(n => n.MessageId == "m3").CreatedAt);
        Assert.Equal(later, all.Single(n => n.MessageId == "m4").CreatedAt);

        Assert.True(service.TryGetKindOverride("m3", out var kind3));
        Assert.Equal(MessageKind.Notice, kind3);
        Assert.True(service.TryGetKindOverride("m4", out var kind4));
        Assert.Equal(MessageKind.Notice, kind4);
    }

    [Fact]
    public async Task MoveDocumentToNoticeAsync_ManualText_MigratesSingleNotice()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var created = DateTimeOffset.Now.AddDays(-1);
        var date = RetentionPolicies.BucketOf(created);
        await homework.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m5"],
            MemberOpenId = "member-5",
            Text = "第 5 页练习题",
            CreatedAt = created
        });
        await homework.SaveDocumentTextAsync(date, "数学", "手工整理后的整篇内容");

        var migrated = await service.MoveDocumentToNoticeAsync(date, "数学");

        Assert.Equal(1, migrated);
        Assert.Empty(await homework.GetDocumentsAsync(date));
        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal($"document:{date:yyyyMMdd}:数学", notice.MessageId);
        // 需求 2：手工文本分支同样继承文档学科标签（落档 Subject 与「数学：」前缀）
        Assert.Equal("数学：手工整理后的整篇内容", notice.Content);
        Assert.Equal("数学", notice.Subject);
        // 需求 5：手工整理文本无独立时间，取文档内最早条目的原始时间
        Assert.Equal(created, notice.CreatedAt);

        // 回归：手工文本分支过去完全不记来源消息覆盖，原消息重投/断点续传补齐时会在作业侧复现
        Assert.True(service.TryGetKindOverride("m5", out var manualKind));
        Assert.Equal(MessageKind.Notice, manualKind);
    }

    [Fact]
    public async Task MoveDocumentToNoticeAsync_MergedEntry_RecordsOverrideForEverySourceMessage()
    {
        // 回归：逐条分支过去只记「第一条」来源消息 id，同一（合并）条目的其余来源消息
        // 在重投/断点续传补齐时会被识别链摆回作业
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        // 固定到「一天前的本地上午 9:00」：避免 AddMinutes 在深夜运行时跨过午夜（见上一条用例）
        var localNow = DateTimeOffset.Now;
        var created = new DateTimeOffset(localNow.Date.AddDays(-1).AddHours(9), localNow.Offset);
        var date = RetentionPolicies.BucketOf(created);
        await homework.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m6"],
            MemberOpenId = "member-6",
            Text = "第 6 页第 1-5 题",
            CreatedAt = created
        });
        // 部分重叠 → 合并进同一 entry，来源消息 id 变成两条
        await homework.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m7"],
            MemberOpenId = "member-6",
            Text = "第 6 页第 1-5 题\n第 6 页第 6-8 题",
            CreatedAt = created.AddMinutes(1)
        });
        var entry = Assert.Single((await homework.GetDocumentsAsync(date)).Single().Entries);
        Assert.Equal(2, entry.SourceMessageIds.Count);

        await service.MoveDocumentToNoticeAsync(date, "数学");

        Assert.True(service.TryGetKindOverride("m6", out var first));
        Assert.Equal(MessageKind.Notice, first);
        Assert.True(service.TryGetKindOverride("m7", out var second));
        Assert.Equal(MessageKind.Notice, second);
    }

    [Fact]
    public async Task Override_PersistsAcrossServiceRestart()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var notice = await notices.AddOrUpdateAsync("m6", "重启后仍按作业落档");
        Assert.True(await service.MoveNoticeToHomeworkAsync(notice.Id, "英语"));

        // 重启：全新 store + 全新服务实例，覆盖记录从磁盘加载
        var restarted = new MessageReclassifyService(new NoticeStore(_dir), new HomeworkStore(_dir), _dir);

        Assert.True(restarted.TryGetKindOverride("m6", out var kind));
        Assert.Equal(MessageKind.Homework, kind);
    }

    [Fact]
    public async Task MoveNonexistent_ReturnsFalseOrZeroWithoutThrowing()
    {
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);

        Assert.False(await service.MoveNoticeToHomeworkAsync(Guid.NewGuid(), "数学"));
        Assert.False(await service.MoveHomeworkToNoticeAsync(Guid.NewGuid()));
        Assert.Equal(0, await service.MoveDocumentToNoticeAsync(
            DateOnly.FromDateTime(DateTime.Now), "不存在的学科"));
        Assert.False(service.TryGetKindOverride("不存在的消息", out _));
    }

    // ============ 需求 2（2.1.0-beta.1）：换类时直接继承学科标签与来源 ============

    [Fact]
    public async Task MoveNoticeToHomeworkAsync_InheritsNoticeSubjectToItemAndDocument()
    {
        // 需求 2 ①：通知已有学科 → 换类不再让用户选，作业条目与学科文档条目继承同学科
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var notice = await notices.AddOrUpdateWithSourceAsync("m10", "第 3 页练习题", "member-10", null, null,
            "张老师", "数学");
        Assert.Equal("数学", notice.Subject);

        // 悬浮窗在通知已有学科时直接把它传进来（不弹学科菜单）
        var moved = await service.MoveNoticeToHomeworkAsync(notice.Id, notice.Subject);

        Assert.True(moved);
        Assert.Empty(await notices.GetAllAsync());

        var item = Assert.Single(await homework.GetBySubjectAsync("数学"));
        Assert.Equal("数学", item.Subject);
        Assert.Equal(SubjectSource.Manual, item.SubjectSource);
        Assert.Equal(1.0, item.SubjectConfidence);
        Assert.Equal(notice.CreatedAt, item.CreatedAt);

        var document = Assert.Single(await homework.GetDocumentsAsync(RetentionPolicies.BucketOf(notice.CreatedAt)));
        Assert.Equal("数学", document.Subject);
        var entry = Assert.Single(document.Entries);
        // 需求 2 ③：来源展示名随通知继承到作业文档条目
        Assert.Equal("张老师", entry.SenderLabel);
        Assert.Equal("member-10", entry.MemberOpenId);
    }

    [Fact]
    public async Task MoveNoticeToHomeworkAsync_ContentPrefixMatchingSubject_StrippedOnce()
    {
        // 需求 2 ②：通知正文的「{学科}：」前缀（NoticeSubjectPrefixer 加的）与继承学科一致时剥掉，
        // 避免作业文档出现「数学：数学作业…」的重复前缀（幂等）
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var notice = await notices.AddOrUpdateWithSourceAsync("m11", "数学作业第 3 页", "member-11", null, null,
            "李老师", "数学");
        Assert.Equal("数学：数学作业第 3 页", notice.Content);

        Assert.True(await service.MoveNoticeToHomeworkAsync(notice.Id, notice.Subject));

        var item = Assert.Single(await homework.GetAllAsync());
        Assert.Equal("数学作业第 3 页", item.Content);

        var document = Assert.Single(await homework.GetDocumentsAsync(RetentionPolicies.BucketOf(notice.CreatedAt)));
        var entry = Assert.Single(document.Entries);
        Assert.Equal("数学作业第 3 页", entry.Text);
        Assert.DoesNotContain("：数学", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveNoticeToHomeworkAsync_SubjectMismatch_KeepsOriginalContent()
    {
        // 需求 2：前缀学科与继承学科不一致（无学科通知经兜底菜单另选了学科）→ 保持原文
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var notice = await notices.AddOrUpdateWithSourceAsync("m12", "背诵第 2 段", "member-12", null, null,
            "王老师", "语文");
        Assert.Equal("语文：背诵第 2 段", notice.Content);

        Assert.True(await service.MoveNoticeToHomeworkAsync(notice.Id, "数学"));

        var item = Assert.Single(await homework.GetBySubjectAsync("数学"));
        Assert.Equal("语文：背诵第 2 段", item.Content);
        var document = Assert.Single(await homework.GetDocumentsAsync(RetentionPolicies.BucketOf(notice.CreatedAt)));
        Assert.Equal("语文：背诵第 2 段", Assert.Single(document.Entries).Text);
    }

    [Fact]
    public async Task MoveHomeworkToNoticeAsync_InheritsSubjectAndSenderLabel()
    {
        // 需求 2 ④：作业 → 通知后通知条目学科正确（落档 Subject 与「学科：」前缀）并继承来源展示名
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var item = await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "m20",
            Content = "背诵第 2 段",
            MemberOpenId = "member-20",
            Subject = "语文",
            CreatedAt = DateTimeOffset.Now
        });
        await homework.AppendDocumentEntryAsync("语文", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m20"],
            MemberOpenId = "member-20",
            SenderLabel = "张老师",
            Text = item.Content,
            CreatedAt = item.CreatedAt
        });

        Assert.True(await service.MoveHomeworkToNoticeAsync(item.Id));

        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("语文", notice.Subject);
        Assert.Equal("语文：背诵第 2 段", notice.Content);
        Assert.Equal("张老师", notice.SenderLabel);
        Assert.Equal("member-20", notice.MemberOpenId);
        Assert.Equal(item.CreatedAt, notice.CreatedAt);
    }

    [Fact]
    public async Task MoveHomeworkToNoticeAsync_ContentAlreadyPrefixed_NoDuplicatedPrefix()
    {
        // 需求 2 ④：正文已带同学科前缀时幂等（不重复加前缀）
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var item = await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "m21",
            Content = "语文：背诵第 2 段",
            MemberOpenId = "member-21",
            Subject = "语文",
            CreatedAt = DateTimeOffset.Now
        });

        Assert.True(await service.MoveHomeworkToNoticeAsync(item.Id));

        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("语文：背诵第 2 段", notice.Content);
        Assert.Equal("语文", notice.Subject);
    }

    [Fact]
    public async Task MoveHomeworkToNoticeAsync_UnclassifiedSubject_NoPlaceholderPrefix()
    {
        // 需求 2：学科为「未分类」时不传显式学科（否则会落出「未分类：」前缀），行为与旧版一致
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var item = await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "m22",
            Content = "待归类内容",
            MemberOpenId = "member-22",
            Subject = HomeworkSubjectResolver.Unclassified,
            CreatedAt = DateTimeOffset.Now
        });

        Assert.True(await service.MoveHomeworkToNoticeAsync(item.Id));

        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("待归类内容", notice.Content);
        Assert.Equal("", notice.Subject);
    }

    [Fact]
    public async Task RoundTrip_HomeworkToNoticeAndBack_KeepsSubjectAndSender()
    {
        // 需求 2 ⑤：作业 → 通知 → 作业 往返后学科与来源保持，正文不因往返出现重复前缀
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        var item = await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "m30",
            Content = "第 5 页练习题",
            MemberOpenId = "member-30",
            Subject = "数学",
            CreatedAt = DateTimeOffset.Now
        });
        await homework.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m30"],
            MemberOpenId = "member-30",
            SenderLabel = "李老师",
            Text = item.Content,
            CreatedAt = item.CreatedAt
        });

        // 作业 → 通知
        Assert.True(await service.MoveHomeworkToNoticeAsync(item.Id));
        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("数学", notice.Subject);
        Assert.Equal("数学：第 5 页练习题", notice.Content);
        Assert.Equal("李老师", notice.SenderLabel);

        // 通知 → 作业
        Assert.True(await service.MoveNoticeToHomeworkAsync(notice.Id, notice.Subject));

        var back = Assert.Single(await homework.GetBySubjectAsync("数学"));
        Assert.Equal("第 5 页练习题", back.Content);
        var document = Assert.Single(await homework.GetDocumentsAsync(RetentionPolicies.BucketOf(back.CreatedAt)));
        var entry = Assert.Single(document.Entries);
        Assert.Equal("第 5 页练习题", entry.Text);
        Assert.Equal("李老师", entry.SenderLabel);
        Assert.Equal("member-30", entry.MemberOpenId);
    }

    [Fact]
    public async Task MoveDocumentToNoticeAsync_EachNoticeInheritsSubjectAndOwnSenderLabel()
    {
        // 需求 2 ⑥：整篇文档 → 通知时每条通知继承文档学科（含前缀）与自己条目的来源展示名
        var notices = new NoticeStore(_dir);
        var homework = new HomeworkStore(_dir);
        var service = new MessageReclassifyService(notices, homework, _dir);
        // 固定到「一天前的本地上午 9:00」：避免 AddMinutes 在深夜运行时跨过午夜（见上文时间陷阱注释）
        var localNow = DateTimeOffset.Now;
        var created = new DateTimeOffset(localNow.Date.AddDays(-1).AddHours(9), localNow.Offset);
        var date = RetentionPolicies.BucketOf(created);
        await homework.AppendDocumentEntryAsync("英语", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m40"],
            MemberOpenId = "member-40",
            SenderLabel = "王老师",
            Text = "背诵第 1 段",
            CreatedAt = created
        });
        await homework.AppendDocumentEntryAsync("英语", new HomeworkDocumentEntry
        {
            SourceMessageIds = ["m41"],
            MemberOpenId = "member-41",
            SenderLabel = "赵老师",
            Text = "抄写单词 10 个",
            CreatedAt = created.AddMinutes(1)
        });

        var migrated = await service.MoveDocumentToNoticeAsync(date, "英语");

        Assert.Equal(2, migrated);
        var all = await notices.GetAllAsync();
        Assert.Equal(2, all.Count);
        var first = all.Single(n => n.MessageId == "m40");
        Assert.Equal("英语", first.Subject);
        Assert.Equal("英语：背诵第 1 段", first.Content);
        Assert.Equal("王老师", first.SenderLabel);
        var second = all.Single(n => n.MessageId == "m41");
        Assert.Equal("英语", second.Subject);
        Assert.Equal("英语：抄写单词 10 个", second.Content);
        Assert.Equal("赵老师", second.SenderLabel);
    }

    [Theory]
    [InlineData("语文：背诵第 2 段", "语文", "背诵第 2 段")]
    [InlineData("背诵第 2 段", "语文", "背诵第 2 段")]
    [InlineData("数学：第 3 页", "语文", "数学：第 3 页")]
    [InlineData("语文背诵第 2 段", "语文", "语文背诵第 2 段")]
    public void StripPrefixMatchingSubject_OnlyStripsMatchingPrefix(string content, string subject, string expected)
    {
        // 需求 2：只剥「前缀学科 == 继承学科」的「{学科}：」（无分隔符不算前缀，学科不一致保留原文）
        Assert.Equal(expected, MessageReclassifyService.StripPrefixMatchingSubject(content, subject));
    }
}
