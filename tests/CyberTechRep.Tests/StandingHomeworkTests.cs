using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 3 常态化作业：清单格式化（勾选项追加到对应学科组末尾）、点「发送」才落档的写入语义
/// （幂等，重复发送不重复落档）、设置持久化（重启不丢）。
/// </summary>
public sealed class StandingHomeworkTests : IDisposable
{
    private readonly string _dir;

    public StandingHomeworkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "standing", Guid.NewGuid().ToString("N"));
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

    private static HomeworkDocument Document(string subject, params string[] lines) => new()
    {
        Date = DateOnly.FromDateTime(DateTime.Now),
        Subject = subject,
        Entries = lines
            .Select((line, i) => new HomeworkDocumentEntry
            {
                Text = line,
                SourceMessageIds = [$"m{i}"],
                MemberOpenId = "t1",
                CreatedAt = DateTimeOffset.Now
            })
            .ToList()
    };

    private static StandingHomeworkItem Standing(string subject, string content, bool enabled = true) =>
        new() { Subject = subject, Content = content, Enabled = enabled };

    [Fact]
    public void FormatDocuments_CheckedStandingItem_AppendedToSubjectGroupEnd()
    {
        var text = HomeworkDigestFormatter.FormatDocuments(
            [Document("数学", "练习册 P12")],
            [Standing("数学", "校本往后做一课")]);

        Assert.Contains("【数学】", text);
        Assert.Contains("1. 练习册 P12", text);
        Assert.Contains("2. 校本往后做一课（常态化）", text);
        Assert.EndsWith(HomeworkDigestFormatter.TailNote, text);
    }

    [Fact]
    public void FormatDocuments_StandingOnlySubject_StillProducesGroup()
    {
        // 当天该学科没有作业，但勾选的常态化作业要写入该学科文档 → 必须成组发送
        var text = HomeworkDigestFormatter.FormatDocuments(
            [],
            [Standing("英语", "背单词 20 个")]);

        Assert.Contains("【英语】", text);
        Assert.Contains("1. 背单词 20 个（常态化）", text);
    }

    [Fact]
    public void FormatDocuments_NoStandingItems_UnchangedFromDocumentRender()
    {
        var text = HomeworkDigestFormatter.FormatDocuments([Document("语文", "背诵古诗", "默写生字")]);

        Assert.Contains("【语文】", text);
        Assert.Contains("1. 背诵古诗", text);
        Assert.Contains("2. 默写生字", text);
        Assert.DoesNotContain("常态化", text);
    }

    [Fact]
    public void FormatDocuments_Empty_IsTailNoteOnly()
    {
        Assert.Equal(HomeworkDigestFormatter.TailNote, HomeworkDigestFormatter.FormatDocuments([]));
    }

    [Fact]
    public void FormatDocuments_BlankStandingItem_IsIgnored()
    {
        var text = HomeworkDigestFormatter.FormatDocuments(
            [Document("数学", "练习册 P12")],
            [Standing("", "空学科"), Standing("数学", "   ")]);

        Assert.DoesNotContain("常态化", text);
    }

    [Fact]
    public async Task Landing_StandingEntry_AppendsToDocumentEnd()
    {
        var store = new HomeworkStore(_dir);
        await store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            Text = "练习册 P12",
            SourceMessageIds = ["m1"],
            MemberOpenId = "t1",
            CreatedAt = DateTimeOffset.Now
        });

        var document = await store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            Text = "校本往后做一课",
            SourceMessageIds = [],
            MemberOpenId = "",
            SenderLabel = "常态化作业",
            CreatedAt = DateTimeOffset.Now,
            IsStanding = true
        });

        Assert.Equal(2, document.Entries.Count);
        Assert.True(document.Entries[1].IsStanding);
        Assert.EndsWith("校本往后做一课", document.Render, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Landing_SameStandingItemTwice_IsIdempotent()
    {
        var store = new HomeworkStore(_dir);
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 2; i++)
        {
            await store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
            {
                Text = "校本往后做一课",
                SourceMessageIds = [],
                MemberOpenId = "",
                SenderLabel = "常态化作业",
                CreatedAt = now,
                IsStanding = true
            });
        }

        var documents = await store.GetDocumentsAsync(DateOnly.FromDateTime(now.LocalDateTime));
        Assert.Single(documents);
        Assert.Single(documents[0].Entries);
    }

    [Fact]
    public void FormatDocuments_NumberingDisabled_OutputsLinesWithoutSerialNumbers()
    {
        var text = HomeworkDigestFormatter.FormatDocuments(
            [Document("数学", "练习册 P12", "口算 20 题")],
            [Standing("数学", "校本往后做一课")],
            numberLines: false);

        Assert.Contains("【数学】", text);
        Assert.Contains("练习册 P12", text);
        Assert.Contains("口算 20 题", text);
        Assert.Contains("校本往后做一课（常态化）", text);
        Assert.DoesNotContain("1. ", text);
        Assert.DoesNotContain("2. ", text);
        Assert.EndsWith(HomeworkDigestFormatter.TailNote, text);
    }

    [Fact]
    public void FormatDocuments_NumberingEnabledByDefault_KeepsSerialNumbers()
    {
        var text = HomeworkDigestFormatter.FormatDocuments([Document("数学", "练习册 P12")]);

        Assert.Contains("1. 练习册 P12", text);
    }

    [Fact]
    public async Task Settings_NumberDigestLines_DefaultOnAndSurvivesReload()
    {
        var service = new SettingsService(_dir);
        Assert.True(service.Current.Connection.NumberDigestLines); // 缺省：自动补填序号打开

        service.Current.Connection.NumberDigestLines = false;
        await service.SaveAsync();

        var reloaded = new SettingsService(_dir);
        Assert.False(reloaded.Current.Connection.NumberDigestLines);
    }

    [Fact]
    public async Task Settings_StandingHomework_SurvivesReload()
    {
        var service = new SettingsService(_dir);
        service.Current.StandingHomework.Items =
        [
            new StandingHomeworkItem { Subject = "数学", Content = "校本往后做一课", Enabled = true },
            new StandingHomeworkItem { Subject = "英语", Content = "背单词 20 个", Enabled = false }
        ];
        await service.SaveAsync();

        var reloaded = new SettingsService(_dir);

        Assert.Equal(2, reloaded.Current.StandingHomework.Items.Count);
        var math = reloaded.Current.StandingHomework.Items[0];
        Assert.Equal("数学", math.Subject);
        Assert.Equal("校本往后做一课", math.Content);
        Assert.True(math.Enabled);
        Assert.False(reloaded.Current.StandingHomework.Items[1].Enabled);
    }

    [Fact]
    public void Settings_Default_StandingHomework_IsEmptyAndEnabledByDefault()
    {
        var service = new SettingsService(_dir);

        Assert.Empty(service.Current.StandingHomework.Items);
        Assert.True(new StandingHomeworkItem().Enabled);
    }
}
