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
    public async Task 两边都命中_作业优先()
    {
        var clf = CreateClassifier();
        // 「通知」命中 Notice，「作业」命中 Homework → 作业优先
        var result = await clf.ClassifyAsync(Msg("重要通知：今晚完成数学作业"));

        Assert.Equal(MessageKind.Homework, result.Kind);
        Assert.Contains("both_hit_homework_priority", result.MatchReason);
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
