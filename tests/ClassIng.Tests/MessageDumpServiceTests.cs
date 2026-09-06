using System.Runtime.Versioning;
using System.Text.Json;
using ClassIng.Plugin.Services.Maintenance;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>
/// 消息日志 dump 导出测试：环形缓冲捕获、JSONL 全字段（含群 OpenID）导出、
/// 按 MessageId 关联分类/学科/文件结果，以及红线（导出内容不含凭据字段）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MessageDumpServiceTests : IDisposable
{
    private readonly string _dir;

    public MessageDumpServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "dump", Guid.NewGuid().ToString("N"));
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

    // ============ 测试替身 ============

    private sealed class FakeIngestService : IMessageIngestService
    {
        public ConnectionStatus Status => ConnectionStatus.Disconnected;

        public event EventHandler<MessageRecord>? MessageReceived;

#pragma warning disable CS0067 // 测试替身：连接状态变化事件不触发
        public event EventHandler<ConnectionStatus>? StatusChanged;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> FetchHistoryAsync(int days, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task ReconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(MessageRecord message) => MessageReceived?.Invoke(this, message);
    }

    private sealed class FakeHomeworkStore : IHomeworkStore
    {
        public List<HomeworkItem> Items { get; } = [];

        public event EventHandler<HomeworkItem>? Changed
        {
            add { }
            remove { }
        }

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
        {
            Items.Add(item);
            return Task.FromResult(item);
        }

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items]);

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items.Where(i => i.Subject == subject)]);

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items]);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            var removed = Items.RemoveAll(i => i.Id == id);
            return Task.FromResult(removed > 0);
        }
    }

    private sealed class FakeNoticeStore : INoticeStore
    {
        public List<NoticeItem> Items { get; } = [];

        public event EventHandler<NoticeItem>? Changed
        {
            add { }
            remove { }
        }

        public Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null,
            CancellationToken ct = default)
        {
            var item = new NoticeItem { MessageId = messageId, Content = content };
            Items.Add(item);
            return Task.FromResult(item);
        }

        public Task MarkReadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkUnreadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items.Where(i => !i.IsRead)]);

        public Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

        public Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeFilePipeline : IFilePipelineService
    {
        public List<FileRecord> Records { get; } = [];

#pragma warning disable CS0067 // 测试替身：文件状态变化事件不触发
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            CancellationToken ct = default)
        {
            var record = new FileRecord { MessageId = messageId, FileName = fileName };
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileRecord>>([.. Records]);
    }

    // ============ 用例 ============

    [Fact]
    public async Task ExportAsync_WritesJsonlWithGroupOpenId_AndJoinsStoresByMessageId()
    {
        var ingest = new FakeIngestService();
        var homework = new FakeHomeworkStore();
        var notices = new FakeNoticeStore();
        var files = new FakeFilePipeline();
        using var service = new MessageDumpService(ingest, homework, notices, files);

        // 消息 A：作业（含学科结果与文件关联）；消息 B：通知；消息 C：未分类
        ingest.Raise(new MessageRecord
        {
            MessageId = "msg-homework",
            GroupOpenId = "GROUP_ABC",
            MemberOpenId = "MEMBER_1",
            SenderNickname = "张三",
            ReceivedAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8)),
            Segments = [new MessageSegment { Type = SegmentTypes.Text, Text = "今天数学作业：口算10页" }],
            RawJsonSnapshot = "{\"group_openid\":\"GROUP_ABC\"}"
        });
        await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "msg-homework",
            Subject = "数学",
            SubjectConfidence = 0.9,
            SubjectSource = SubjectSource.KeywordRule,
            Content = "今天数学作业：口算10页"
        });
        await files.EnqueueAsync("msg-homework", "练习.pdf", "https://example.com/a.pdf");
        ingest.Raise(new MessageRecord
        {
            MessageId = "msg-notice",
            GroupOpenId = "GROUP_DEF",
            ReceivedAt = new DateTimeOffset(2026, 9, 1, 8, 5, 0, TimeSpan.FromHours(8)),
            Segments = [new MessageSegment { Type = SegmentTypes.Text, Text = "明天早自习提前十分钟" }],
            RawJsonSnapshot = "{}"
        });
        await notices.AddOrUpdateAsync("msg-notice", "明天早自习提前十分钟");
        ingest.Raise(new MessageRecord
        {
            MessageId = "msg-unknown",
            GroupOpenId = "GROUP_ABC",
            Segments = [new MessageSegment { Type = SegmentTypes.Text, Text = "收到" }]
        });

        Assert.Equal(3, service.CapturedCount);

        var path = Path.Combine(_dir, "dump.jsonl");
        var count = await service.ExportAsync(path);
        Assert.Equal(3, count);

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        var rows = lines
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToArray();

        var homeworkRow = rows.Single(r => r.GetProperty("MessageId").GetString() == "msg-homework");
        Assert.Equal("GROUP_ABC", homeworkRow.GetProperty("GroupOpenId").GetString());
        Assert.Equal("MEMBER_1", homeworkRow.GetProperty("MemberOpenId").GetString());
        Assert.Equal("张三", homeworkRow.GetProperty("SenderNickname").GetString());
        Assert.Equal("Homework", homeworkRow.GetProperty("Kind").GetString());
        Assert.Equal("数学", homeworkRow.GetProperty("Subject").GetProperty("Subject").GetString());
        Assert.Equal("KeywordRule", homeworkRow.GetProperty("Subject").GetProperty("Source").GetString());
        Assert.Equal(1, homeworkRow.GetProperty("Files").GetArrayLength());
        Assert.Equal("练习.pdf", homeworkRow.GetProperty("Files")[0].GetProperty("FileName").GetString());
        Assert.Equal("今天数学作业：口算10页",
            homeworkRow.GetProperty("Segments")[0].GetProperty("Text").GetString());

        var noticeRow = rows.Single(r => r.GetProperty("MessageId").GetString() == "msg-notice");
        Assert.Equal("GROUP_DEF", noticeRow.GetProperty("GroupOpenId").GetString());
        Assert.Equal("Notice", noticeRow.GetProperty("Kind").GetString());
        Assert.True(noticeRow.GetProperty("Subject").ValueKind == JsonValueKind.Null);

        var unknownRow = rows.Single(r => r.GetProperty("MessageId").GetString() == "msg-unknown");
        Assert.Equal("GROUP_ABC", unknownRow.GetProperty("GroupOpenId").GetString());
        Assert.Equal("Unknown", unknownRow.GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task ExportAsync_DoesNotContainCredentialFields()
    {
        // 红线：dump 内容不含 AppId/AppSecret/Token/ApiKey 字段与凭据值
        var ingest = new FakeIngestService();
        using var service = new MessageDumpService(ingest);
        ingest.Raise(new MessageRecord
        {
            MessageId = "msg-1",
            GroupOpenId = "GROUP_X",
            Segments = [new MessageSegment { Type = SegmentTypes.Text, Text = "hello" }],
            RawJsonSnapshot = "{\"group_openid\":\"GROUP_X\",\"content\":\"hello\"}"
        });

        var path = Path.Combine(_dir, "dump.jsonl");
        await service.ExportAsync(path);
        var text = File.ReadAllText(path);

        string[] forbidden = ["AppId", "AppSecret", "AppSecretProtected", "Token", "ApiKey", "AccessToken"];
        foreach (var word in forbidden)
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExportAsync_EmptyBuffer_WritesEmptyFile_AndReturnsZero()
    {
        using var service = new MessageDumpService(new FakeIngestService());
        var path = Path.Combine(_dir, "empty.jsonl");
        var count = await service.ExportAsync(path);

        Assert.Equal(0, count);
        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllText(path));
    }

    [Fact]
    public async Task ExportAsync_RingBufferKeepsOnlyMostRecentRecords()
    {
        var ingest = new FakeIngestService();
        using var service = new MessageDumpService(ingest);

        const int total = MessageDumpService.MaxCaptured + 50;
        for (var i = 0; i < total; i++)
        {
            ingest.Raise(new MessageRecord
            {
                MessageId = $"msg-{i}",
                GroupOpenId = "GROUP_RING",
                Segments = []
            });
        }

        Assert.Equal(MessageDumpService.MaxCaptured, service.CapturedCount);

        var path = Path.Combine(_dir, "ring.jsonl");
        var count = await service.ExportAsync(path);
        Assert.Equal(MessageDumpService.MaxCaptured, count);

        var lines = File.ReadAllLines(path);
        // 环形缓冲：最早的 50 条被淘汰，首行应为第 50 条
        Assert.Contains("\"MessageId\":\"msg-50\"", lines[0]);
        Assert.Contains("\"MessageId\":\"msg-" + (total - 1) + "\"", lines[^1]);
    }

    [Fact]
    public async Task ExportAsync_StoreReadFailure_DoesNotFailExport()
    {
        var ingest = new FakeIngestService();
        var failingHomework = new ThrowingHomeworkStore();
        using var service = new MessageDumpService(ingest, failingHomework);
        ingest.Raise(new MessageRecord
        {
            MessageId = "msg-1",
            GroupOpenId = "GROUP_OK",
            Segments = []
        });

        var path = Path.Combine(_dir, "degraded.jsonl");
        var count = await service.ExportAsync(path);

        Assert.Equal(1, count);
        var row = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("GROUP_OK", row.GetProperty("GroupOpenId").GetString());
        // 作业侧读取失败 → Kind 落到 Unknown（通知/文件侧仍正常关联）
        Assert.Equal("Unknown", row.GetProperty("Kind").GetString());
    }

    private sealed class ThrowingHomeworkStore : IHomeworkStore
    {
        public event EventHandler<HomeworkItem>? Changed
        {
            add { }
            remove { }
        }

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<int> CleanupAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
