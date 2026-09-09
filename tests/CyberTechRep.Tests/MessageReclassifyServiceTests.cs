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
            Text = item.Content,
            CreatedAt = item.CreatedAt
        });

        var moved = await service.MoveHomeworkToNoticeAsync(item.Id);

        Assert.True(moved);
        Assert.Empty(await homework.GetAllAsync());
        Assert.Empty(await homework.GetDocumentsAsync(date));

        var notice = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("m2", notice.MessageId);
        Assert.Equal("语文背诵第 2 段", notice.Content);
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
        var created = DateTimeOffset.Now.AddDays(-2);
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
        Assert.Contains(all, n => n.MessageId == "m3" && n.Content == "第 3 页练习题" && n.MemberOpenId == "member-3");
        Assert.Contains(all, n => n.MessageId == "m4" && n.Content == "第 4 页练习题" && n.MemberOpenId == "member-4");
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
        Assert.Equal("手工整理后的整篇内容", notice.Content);
        // 需求 5：手工整理文本无独立时间，取文档内最早条目的原始时间
        Assert.Equal(created, notice.CreatedAt);
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
}
