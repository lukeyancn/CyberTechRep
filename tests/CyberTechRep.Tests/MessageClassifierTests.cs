using CyberTechRep.Plugin.Services.Classification;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>模块 2 单元测试：关键词规则分类器（分流 / 优先级 / 热重载 / 防错）。</summary>
public class MessageClassifierTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ITestOutputHelper _output;
    private ClassificationSettings _settings;

    public MessageClassifierTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _settings = new ClassificationSettings();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败忽略
        }
    }

    private KeywordMessageClassifier CreateClassifier() =>
        new(
            new ClassifierOptionsProvider
            {
                GetSettings = () => _settings,
                DataDirectory = _dataDir
            },
            new XunitLogger(_output));

    private static MessageRecord Msg(string text, string id = "m1", params MessageSegment[] extra) =>
        new()
        {
            MessageId = id,
            GroupOpenId = "grp-1",
            MemberOpenId = "mem-1",
            ReceivedAt = DateTimeOffset.UtcNow,
            Segments =
            [
                new MessageSegment { Type = SegmentTypes.Text, Text = text },
                ..extra
            ]
        };

    [Fact]
    public async Task 通知关键词命中_分流为Notice()
    {
        var clf = CreateClassifier();
        var result = await clf.ClassifyAsync(Msg("请注意明天家长会通知"));

        Assert.Equal(MessageKind.Notice, result.Kind);
        Assert.Equal(1.0, result.Confidence);
        Assert.Contains("notice_hit", result.MatchReason);
    }

    [Fact]
    public async Task 作业关键词命中_分流为Homework()
    {
        var clf = CreateClassifier();
        var result = await clf.ClassifyAsync(Msg("明天交数学练习册"));

        Assert.Equal(MessageKind.Homework, result.Kind);
        Assert.Equal(1.0, result.Confidence);
        Assert.Contains("homework_hit", result.MatchReason);
    }

    [Fact]
    public async Task 两边都不命中_返回Unknown()
    {
        var clf = CreateClassifier();
        var result = await clf.ClassifyAsync(Msg("今天天气不错，班级群闲聊"));

        Assert.Equal(MessageKind.Unknown, result.Kind);
        Assert.Equal(0, result.Confidence);
        Assert.Equal("no_keyword_hit", result.MatchReason);
    }

    [Fact]
    public async Task 两边都命中_计数多者胜_作业计数高时判作业()
    {
        var clf = CreateClassifier();
        // 「通知」命中 Notice（1 次）；「完成」「作业」各命中 Homework（2 次）→ 计数 2 > 1 判作业
        var result = await clf.ClassifyAsync(Msg("重要通知：今晚完成数学作业"));

        Assert.Equal(MessageKind.Homework, result.Kind);
        Assert.Contains("homework_count_win", result.MatchReason);
        Assert.Contains("homework_count=2", result.MatchReason);
        Assert.Contains("notice_count=1", result.MatchReason);
    }

    [Fact]
    public async Task 两边都命中_通知计数高时判通知()
    {
        var clf = CreateClassifier();
        // 「注意」「通知」命中 Notice（2 次）；「作业」命中 Homework（1 次）→ 计数 2 > 1 判通知
        var result = await clf.ClassifyAsync(Msg("请注意通知：明天交作业"));

        Assert.Equal(MessageKind.Notice, result.Kind);
        Assert.Contains("notice_count_win", result.MatchReason);
        Assert.Contains("notice_count=2", result.MatchReason);
        Assert.Contains("homework_count=1", result.MatchReason);
    }

    [Fact]
    public async Task 两边都命中_计数相等_平局默认判通知()
    {
        var clf = CreateClassifier();
        // 「注意」命中 Notice（1 次）；「完成」命中 Homework（1 次）→ 平局 → 通知（TieBreakNoticeWins）
        var result = await clf.ClassifyAsync(Msg("请注意按时完成"));

        Assert.Equal(MessageKind.Notice, result.Kind);
        Assert.Equal(1.0, result.Confidence);
        Assert.Contains("count_tie_notice_default", result.MatchReason);
    }

    [Fact]
    public async Task 同一关键词多次出现_每次出现都计数()
    {
        var clf = CreateClassifier();
        // 「作业」出现 3 次（homework=3）>「注意」1 次（notice=1）→ 作业；
        // 同一关键词「作业」的多次出现全部计入
        var result = await clf.ClassifyAsync(Msg("作业请注意：先交作业，再检查作业"));

        Assert.Equal(MessageKind.Homework, result.Kind);
        Assert.Contains("homework_count=3", result.MatchReason);
        Assert.Contains("notice_count=1", result.MatchReason);
    }

    [Fact]
    public async Task 大小写不敏感_命中仍计数()
    {
        _settings = new ClassificationSettings
        {
            NoticeKeywords = ["Notice"],
            HomeworkKeywords = ["Hw"]
        };
        var clf = CreateClassifier();
        // 大小写不敏感：NOTICE / hw 均命中；hw 出现 2 次 > notice 1 次 → 作业
        var result = await clf.ClassifyAsync(Msg("please NOTICE me, hw and HW"));

        Assert.Equal(MessageKind.Homework, result.Kind);
        Assert.Contains("homework_count=2", result.MatchReason);
        Assert.Contains("notice_count=1", result.MatchReason);
    }

    [Fact]
    public async Task ReloadRules_热生效_修改JSON后立即生效()
    {
        var clf = CreateClassifier();
        var before = await clf.ClassifyAsync(Msg("紧急班会集合"));
        Assert.Equal(MessageKind.Unknown, before.Kind);

        var path = Path.Combine(_dataDir, "classification-keywords.json");
        await File.WriteAllTextAsync(path, """
            {
              "noticeKeywords": ["班会", "集合"],
              "homeworkKeywords": ["作业"]
            }
            """);
        clf.ReloadRules();

        var after = await clf.ClassifyAsync(Msg("紧急班会集合"));
        Assert.Equal(MessageKind.Notice, after.Kind);
        Assert.Contains("班会", after.MatchReason);
    }

    [Fact]
    public async Task ReloadRules_设置默认值变更_无JSON时种子后可读()
    {
        // 删除可能已种子的文件，改用自定义默认设置再 Reload
        var path = Path.Combine(_dataDir, "classification-keywords.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _settings = new ClassificationSettings
        {
            NoticeKeywords = ["校运会"],
            HomeworkKeywords = ["默写"]
        };
        var clf = CreateClassifier();

        var result = await clf.ClassifyAsync(Msg("下周一校运会"));
        Assert.Equal(MessageKind.Notice, result.Kind);
        Assert.True(File.Exists(path), "首次加载应种子外置 JSON");
    }

    [Fact]
    public async Task 纯媒体无文本_返回Unknown()
    {
        var clf = CreateClassifier();
        var msg = new MessageRecord
        {
            MessageId = "media-1",
            GroupOpenId = "grp-1",
            ReceivedAt = DateTimeOffset.UtcNow,
            Segments =
            [
                new MessageSegment
                {
                    Type = SegmentTypes.Image,
                    Url = "https://example.com/a.png",
                    FileName = "a.png"
                },
                new MessageSegment
                {
                    Type = SegmentTypes.File,
                    Url = "https://example.com/hw.pdf",
                    FileName = "作业.pdf"
                }
            ]
        };

        var result = await clf.ClassifyAsync(msg);
        Assert.Equal(MessageKind.Unknown, result.Kind);
        Assert.Equal("empty_text_with_media", result.MatchReason);
    }

    [Fact]
    public async Task 多文本段拼接_可命中跨段关键词()
    {
        var clf = CreateClassifier();
        var msg = new MessageRecord
        {
            MessageId = "seg-1",
            GroupOpenId = "grp-1",
            ReceivedAt = DateTimeOffset.UtcNow,
            Segments =
            [
                new MessageSegment { Type = SegmentTypes.Text, Text = "请各位同学" },
                new MessageSegment { Type = SegmentTypes.At, Text = "@全体成员" },
                new MessageSegment { Type = SegmentTypes.Text, Text = "按时提交报告" }
            ]
        };

        var result = await clf.ClassifyAsync(msg);
        Assert.Equal(MessageKind.Homework, result.Kind); // 「提交」
    }

    [Fact]
    public async Task 异常输入_空Segments与损坏规则_不崩溃()
    {
        var clf = CreateClassifier();

        var emptySeg = new MessageRecord
        {
            MessageId = "e1",
            GroupOpenId = "g",
            ReceivedAt = DateTimeOffset.UtcNow,
            Segments = []
        };
        var r1 = await clf.ClassifyAsync(emptySeg);
        Assert.Equal(MessageKind.Unknown, r1.Kind);

        // 写入非法 JSON，Reload 应吞掉异常并保留旧规则
        var path = Path.Combine(_dataDir, "classification-keywords.json");
        await File.WriteAllTextAsync(path, "{ not-json !!!");
        clf.ReloadRules(); // 不得抛出

        var r2 = await clf.ClassifyAsync(Msg("明天交作业"));
        Assert.Equal(MessageKind.Homework, r2.Kind);
    }

    [Fact]
    public void ExtractPlainText_只拼接text段()
    {
        var text = KeywordMessageClassifier.ExtractPlainText(
        [
            new MessageSegment { Type = SegmentTypes.Text, Text = "A" },
            new MessageSegment { Type = SegmentTypes.Image, Text = "忽略" },
            new MessageSegment { Type = SegmentTypes.Text, Text = "B" }
        ]);
        Assert.Equal("AB", text);
        Assert.Equal("", KeywordMessageClassifier.ExtractPlainText(null));
    }
}
