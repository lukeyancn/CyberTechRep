using System.Runtime.Versioning;
using CyberTechRep.Plugin.Services.Classification;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Pipeline;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 时间口径测试：通知 / 作业 / 学科文档 / 附件记录的落档时间 = 消息在群里的真实发送时间
/// （<see cref="MessageRecord.ReceivedAt"/>：协议端发送时间优先、本机接收时间兜底）。
/// <para>
/// 覆盖：两种接入模式的时间戳解析（NapCat Unix 秒 / QQ 官方平台 RFC3339）、写入侧贯通、
/// 重投幂等（不刷新已落档时间）、重试载荷携带原始时间与旧载荷回落、接口默认实现向后兼容、
/// 附件记录时间的新旧重载行为。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MessageTimeSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public MessageTimeSourceTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "time-source", Guid.NewGuid().ToString("N"));
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

    /// <summary>固定的「群里真实发送时间」：2026-09-18 23:39:00 +08:00（官方 RFC3339 表示）。</summary>
    private static readonly DateTimeOffset SentAt =
        new(2026, 9, 18, 23, 39, 0, TimeSpan.FromHours(8));

    private static readonly long SentAtUnix = SentAt.ToUnixTimeSeconds();

    // ============ ① 时间戳解析：Unix 秒 + RFC3339/ISO8601 ============

    [Fact]
    public void 解析_Unix秒字符串_按原值返回()
    {
        // NapCat 路径（OneBot time 透传）：语义与旧版一致
        Assert.Equal(1725710001, MessageIngestPipeline.ParseSourceTimestamp("1725710001"));
    }

    [Fact]
    public void 解析_RFC3339带时区偏移_返回对应Unix秒()
    {
        // QQ 官方平台路径：带 +08:00 偏移
        Assert.Equal(SentAtUnix, MessageIngestPipeline.ParseSourceTimestamp("2026-09-18T23:39:00+08:00"));
    }

    [Fact]
    public void 解析_RFC3339_Z形式_返回对应Unix秒()
    {
        // QQ 官方平台路径：UTC 的 Z 形式（与 +08:00 表示同一时刻）
        Assert.Equal(SentAtUnix, MessageIngestPipeline.ParseSourceTimestamp("2026-09-18T15:39:00Z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不是时间")]
    [InlineData("1999-12-31T23:59:59Z")] // 早于 2000-01-01 → 越界
    [InlineData("946684799")]            // 2000-01-01 前 1 秒 → 越界
    [InlineData("-1")]                   // 负数 → 越界
    public void 解析_非法或越界_返回0(string? raw)
    {
        Assert.Equal(0, MessageIngestPipeline.ParseSourceTimestamp(raw));
    }

    [Fact]
    public void 解析_未来超过一天_返回0()
    {
        var future = DateTimeOffset.UtcNow.AddDays(3);
        Assert.Equal(0, MessageIngestPipeline.ParseSourceTimestamp(future.ToString("O")));

        // 边界内（明天之前）的有效时间不被拒绝
        var ok = DateTimeOffset.UtcNow.AddHours(1);
        Assert.True(MessageIngestPipeline.ParseSourceTimestamp(ok.ToString("O")) > 0);
    }

    [Fact]
    public void 映射_官方RFC3339时间戳_ReceivedAt为群里发送时间()
    {
        // 官方平台事件 JSON：timestamp 为 RFC3339 字符串（旧版解析失败 → 回落本机接收时间）
        var pipeline = new MessageIngestPipeline(new MessageIdempotencyStore(_dir), new XunitLogger(_output));
        var received = new List<MessageRecord>();
        pipeline.MessageReceived += (_, m) => received.Add(m);

        var result = pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, """
            {
              "id": "msg-time-rfc3339",
              "group_openid": "grp-1",
              "author": { "member_openid": "mem-1", "username": "课代表" },
              "content": "明天交数学作业",
              "timestamp": "2026-09-18T23:39:00+08:00"
            }
            """);

        Assert.Equal(PipelineAction.Accepted, result.Action);
        var record = Assert.Single(received);
        Assert.Equal(SentAtUnix, record.SourceTimestampUnix);
        Assert.Equal(SentAt, record.ReceivedAt); // DateTimeOffset 按同一时刻比较（时区无关）
    }

    [Fact]
    public void 映射_NapCatUnix秒时间戳_ReceivedAt为群里发送时间()
    {
        var pipeline = new MessageIngestPipeline(new MessageIdempotencyStore(_dir), new XunitLogger(_output));
        var received = new List<MessageRecord>();
        pipeline.MessageReceived += (_, m) => received.Add(m);

        var result = pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, """
            {
              "id": "msg-time-unix",
              "group_openid": "grp-1",
              "author": { "member_openid": "mem-1", "username": "课代表" },
              "content": "明天交数学作业",
              "timestamp": "1725710001"
            }
            """);

        Assert.Equal(PipelineAction.Accepted, result.Action);
        var record = Assert.Single(received);
        Assert.Equal(1725710001, record.SourceTimestampUnix);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1725710001).ToLocalTime(), record.ReceivedAt);
    }

    [Fact]
    public void 映射_无时间戳字段_ReceivedAt回落本机接收时刻()
    {
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var pipeline = new MessageIngestPipeline(new MessageIdempotencyStore(_dir), new XunitLogger(_output));
        var received = new List<MessageRecord>();
        pipeline.MessageReceived += (_, m) => received.Add(m);

        var result = pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, """
            {
              "id": "msg-time-missing",
              "group_openid": "grp-1",
              "content": "无时间戳消息"
            }
            """);

        Assert.Equal(PipelineAction.Accepted, result.Action);
        var record = Assert.Single(received);
        Assert.Equal(0, record.SourceTimestampUnix);
        Assert.True(record.ReceivedAt >= before, "无协议端时间时应回落到本机接收时刻");
    }

    // ============ ② 端到端：发送时间贯通通知 / 作业 / 文档 / 附件 ============

    [Fact]
    public async Task 通知落档_使用消息在群里的发送时间()
    {
        var notices = new RecordingNoticeStore();
        var dispatch = CreateDispatch(MessageKind.Notice, notices: notices);

        await dispatch.ProcessMessageAsync(Message("m-notice-time", SentAt, Text("停课通知：明天放假")));

        var item = Assert.Single(notices.Items);
        Assert.Equal(SentAt, item.CreatedAt);
        dispatch.Dispose();
    }

    [Fact]
    public async Task 作业落档_条目与文档条目与附件记录_均使用发送时间()
    {
        var homework = new RecordingHomeworkStore();
        var files = new RecordingFilePipeline();
        var dispatch = CreateDispatch(MessageKind.Homework, homework: homework, files: files);

        var message = Message("m-homework-time", SentAt, Text("明天交数学作业"),
            new MessageSegment
            {
                Type = SegmentTypes.File,
                Url = "https://example.com/hw.pdf",
                FileName = "hw.pdf"
            });

        await dispatch.ProcessMessageAsync(message);

        var item = Assert.Single(homework.Items);
        Assert.Equal(SentAt, item.CreatedAt);

        var (_, entry) = Assert.Single(homework.DocumentAppendCalls);
        Assert.Equal(SentAt, entry.CreatedAt);

        // 附件入队走带时间的重载（旧重载语义：null = 本机当前时间）
        Assert.Equal(SentAt, Assert.Single(files.EnqueuedCreatedAts));
        dispatch.Dispose();
    }

    [Fact]
    public async Task 端到端_官方RFC3339事件到落档_通知时间等于群里发送时间()
    {
        // 完整链路：协议事件（RFC3339 timestamp）→ 接入管道映射 ReceivedAt → 分发落档
        var pipeline = new MessageIngestPipeline(new MessageIdempotencyStore(_dir), new XunitLogger(_output));
        var received = new List<MessageRecord>();
        pipeline.MessageReceived += (_, m) => received.Add(m);

        pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, """
            {
              "id": "msg-e2e-rfc3339",
              "group_openid": "grp-1",
              "author": { "member_openid": "mem-1", "username": "课代表" },
              "content": "停课通知：明天放假",
              "timestamp": "2026-09-18T23:39:00+08:00"
            }
            """);

        var notices = new RecordingNoticeStore();
        var dispatch = CreateDispatch(MessageKind.Notice, notices: notices);
        await dispatch.ProcessMessageAsync(Assert.Single(received));

        Assert.Equal(SentAt, Assert.Single(notices.Items).CreatedAt);
        dispatch.Dispose();
    }

    [Fact]
    public async Task 端到端_NapCatUnix秒事件到落档_作业时间等于群里发送时间()
    {
        var pipeline = new MessageIngestPipeline(new MessageIdempotencyStore(_dir), new XunitLogger(_output));
        var received = new List<MessageRecord>();
        pipeline.MessageReceived += (_, m) => received.Add(m);

        pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, """
            {
              "id": "msg-e2e-unix",
              "group_openid": "grp-1",
              "author": { "member_openid": "mem-1", "username": "课代表" },
              "content": "明天交数学作业",
              "timestamp": "1725710001"
            }
            """);

        var homework = new RecordingHomeworkStore();
        var dispatch = CreateDispatch(MessageKind.Homework, homework: homework);
        await dispatch.ProcessMessageAsync(Assert.Single(received));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1725710001).ToLocalTime(),
            Assert.Single(homework.Items).CreatedAt);
        dispatch.Dispose();
    }

    [Fact]
    public async Task 源时间为零的消息_落档时间仍取ReceivedAt兜底值()
    {
        // SourceTimestampUnix == 0（协议端未提供）时 MessageIngestPipeline 已把 ReceivedAt 置为本机接收时刻，
        // 写入侧一律以 ReceivedAt 为唯一来源：条目时间不早于测试开始时刻。
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var notices = new RecordingNoticeStore();
        var dispatch = CreateDispatch(MessageKind.Notice, notices: notices);

        await dispatch.ProcessMessageAsync(new MessageRecord
        {
            MessageId = "m-no-source-time",
            GroupOpenId = "grp-1",
            ReceivedAt = DateTimeOffset.Now,
            SourceTimestampUnix = 0,
            Segments = [Text("停课通知：明天放假")]
        });

        var item = Assert.Single(notices.Items);
        Assert.True(item.CreatedAt >= before, "无协议端时间时应采用接收时刻兜底");
        dispatch.Dispose();
    }

    // ============ ③ 幂等：重投 / 补录不得刷新已落档时间（真实存储） ============

    [Fact]
    public async Task 重投同一条消息_作业与文档时间不被刷新()
    {
        var homework = new HomeworkStore(_dir);
        var dispatch = CreateDispatch(MessageKind.Homework, homework: homework);

        await dispatch.ProcessMessageAsync(Message("m-dup-homework", SentAt, Text("明天交数学作业")));

        // 同 MessageId 重投（ReceivedAt 更晚）：HomeworkStore.Merge 保留原 CreatedAt
        var later = SentAt.AddHours(3);
        await dispatch.ProcessMessageAsync(Message("m-dup-homework", later, Text("明天交数学作业")));

        var item = Assert.Single(await homework.GetAllAsync());
        Assert.Equal(SentAt, item.CreatedAt);

        var documents = await homework.GetDocumentsAsync(RetentionPolicies.BucketOf(SentAt));
        var document = Assert.Single(documents);
        var entry = Assert.Single(document.Entries);
        Assert.Equal(SentAt, entry.CreatedAt);
        dispatch.Dispose();
    }

    [Fact]
    public async Task 重投同一条消息_通知时间不被刷新()
    {
        var notices = new NoticeStore(_dir);
        var dispatch = CreateDispatch(MessageKind.Notice, notices: notices);

        await dispatch.ProcessMessageAsync(Message("m-dup-notice", SentAt, Text("停课通知：明天放假")));
        await dispatch.ProcessMessageAsync(Message("m-dup-notice", SentAt.AddHours(2), Text("停课通知：明天放假")));

        var item = Assert.Single(await notices.GetAllAsync());
        Assert.Equal(SentAt, item.CreatedAt);
        dispatch.Dispose();
    }

    // ============ ④ 重试补写：载荷携带原始发送时间；旧载荷回落补写时刻 ============

    [Fact]
    public async Task SubjectClassify重试_载荷携带发送时间_补写条目沿用发送时间()
    {
        var retry = CreateRetryQueue();
        var homework = new RecordingHomeworkStore();
        var dispatch = CreateDispatch(MessageKind.Homework, homework: homework, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        await retry.EnqueueAsync(
            RetryOperationType.SubjectClassify,
            MessageDispatchService.BuildSubjectClassifyPayload("m-retry-subject", "背单词二十个", "mem-1", SentAt));
        await retry.TickAsync(forceDue: true);

        var item = Assert.Single(homework.Items);
        Assert.Equal(SentAt, item.CreatedAt);
        dispatch.Dispose();
    }

    [Fact]
    public async Task StoreWrite重试_载荷携带发送时间_通知条目沿用发送时间()
    {
        var retry = CreateRetryQueue();
        var notices = new RecordingNoticeStore();
        var dispatch = CreateDispatch(MessageKind.Notice, notices: notices, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        var payload = new MessageDispatchService.StoreWritePayload(
            MessageDispatchService.StoreWriteKind.NoticeUpsert, "m-retry-notice", "重放的通知", null,
            CreatedAt: SentAt);
        await retry.EnqueueAsync(RetryOperationType.StoreWrite, payload);
        await retry.TickAsync(forceDue: true);

        Assert.Equal(SentAt, Assert.Single(notices.Items).CreatedAt);
        dispatch.Dispose();
    }

    [Fact]
    public async Task StoreWrite重试_旧载荷无时间字段_回落补写时刻不崩溃()
    {
        // 旧版本持久化的载荷 JSON（无 createdAt 字段）：反序列化得 null → 回落补写时刻（向后兼容）
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var retry = CreateRetryQueue();
        var notices = new RecordingNoticeStore();
        var dispatch = CreateDispatch(MessageKind.Notice, notices: notices, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        await retry.EnqueueAsync(RetryOperationType.StoreWrite,
            "{\"Kind\":0,\"MessageId\":\"m-legacy-notice\",\"Content\":\"旧载荷通知\",\"Subject\":null," +
            "\"MemberOpenId\":null,\"GroupOpenId\":null}");
        await retry.TickAsync(forceDue: true);

        var item = Assert.Single(notices.Items);
        Assert.True(item.CreatedAt >= before, "旧载荷应回落补写时刻");
        dispatch.Dispose();
    }

    [Fact]
    public void 重试载荷_时间字段序列化往返保持()
    {
        var json = MessageDispatchService.BuildSubjectClassifyPayload("m1", "文本", "mem-1", SentAt);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(SentAtUnix,
            doc.RootElement.GetProperty("CreatedAt").GetDateTimeOffset().ToUnixTimeSeconds());
    }

    // ============ ⑤ 附件：新重载用传入时间、旧重载用当前时刻（真实文件管道） ============

    [Fact]
    public async Task 附件入队_新重载传入发送时间_记录使用该时间()
    {
        var service = CreateFileService();

        // url 为空 → 记录创建后即失败早退（不发起网络请求），恰好可验证记录时间
        var record = await service.EnqueueAsync("m-file-time", "a.pdf", url: null,
            memberOpenId: "mem-1", groupOpenId: "grp-1", createdAt: SentAt);

        Assert.Equal(SentAt, record.CreatedAt);
    }

    [Fact]
    public async Task 附件入队_旧重载未传时间_记录使用当前时刻()
    {
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var service = CreateFileService();

        var record = await service.EnqueueAsync("m-file-now", "b.pdf", url: null,
            memberOpenId: "mem-1", groupOpenId: "grp-1");

        Assert.True(record.CreatedAt >= before, "未传时间时应使用本机当前时刻");
        Assert.True(DateTimeOffset.Now - record.CreatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task 附件入队_复用既有失败记录_不刷新首见时间()
    {
        var service = CreateFileService();

        // 首次入队（发送时间）→ 因缺 Url 失败；再次入队（更晚的发送时间）时复用同一条记录
        var first = await service.EnqueueAsync("m-file-retry", "c.pdf", url: null,
            memberOpenId: "mem-1", groupOpenId: "grp-1", createdAt: SentAt);
        var second = await service.EnqueueAsync("m-file-retry", "c.pdf", url: null,
            memberOpenId: "mem-1", groupOpenId: "grp-1", createdAt: SentAt.AddHours(5));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(SentAt, second.CreatedAt);
    }

    [Fact]
    public void 接口默认实现_仅实现旧签名的替身_新重载回落旧签名()
    {
        // 既有测试替身只实现旧签名：新重载必须经 DIM 回落到旧签名（不破坏既有实现/替身）
        IFilePipelineService pipeline = new LegacyOnlyFilePipeline();
        var record = pipeline.EnqueueAsync("m", "legacy.pdf", "https://example.com/x.pdf",
            "mem-1", "grp-1", SentAt).GetAwaiter().GetResult();
        Assert.Equal("legacy.pdf", record.FileName);
    }

    // ============ 构造辅助 / 测试替身 ============

    private static MessageSegment Text(string text) => new() { Type = SegmentTypes.Text, Text = text };

    private static MessageRecord Message(string id, DateTimeOffset receivedAt, params MessageSegment[] segments) => new()
    {
        MessageId = id,
        GroupOpenId = "grp-1",
        MemberOpenId = "mem-1",
        SenderNickname = "课代表",
        ReceivedAt = receivedAt,
        SourceTimestampUnix = receivedAt.ToUnixTimeSeconds(),
        Segments = segments
    };

    private RetryQueueService CreateRetryQueue() => new(new RetryQueueOptions
    {
        GetSettings = () => new MaintenanceSettings(),
        DataDirectory = _dir,
        AutoStartScheduler = false
    });

    private FilePipelineService CreateFileService()
    {
        var provider = new FilePipelineOptionsProvider
        {
            GetSettings = () => new FileSettings(),
            DataDirectory = _dir
        };
        return new FilePipelineService(provider, new HttpClient());
    }

    private static MessageDispatchService CreateDispatch(
        MessageKind kind,
        INoticeStore? notices = null,
        IHomeworkStore? homework = null,
        IFilePipelineService? files = null,
        RetryQueueService? retryQueue = null) =>
        new(
            classifier: new FakeKindClassifier(kind),
            subjectChain: new FakeSubjectChain(),
            noticeStore: notices,
            homeworkStore: homework,
            filePipeline: files,
            retryQueue: retryQueue);

    /// <summary>固定返回指定消息类型的分类器替身。</summary>
    private sealed class FakeKindClassifier(MessageKind kind) : IMessageClassifier
    {
        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default)
            => Task.FromResult(new ClassifiedMessage
            {
                Source = message, Kind = kind, Confidence = 1.0, MatchReason = "test"
            });

        public void ReloadRules()
        {
        }
    }

    /// <summary>固定返回「数学」的学科识别链替身。</summary>
    private sealed class FakeSubjectChain : ISubjectClassifierChain
    {
        public Task<SubjectResult> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
            => Task.FromResult(new SubjectResult
            {
                Subject = "数学", Confidence = 1.0, Source = SubjectSource.KeywordRule
            });
    }

    /// <summary>记录写入时间的通知存储替身（含基础幂等：同 MessageId 不刷新创建时间）。</summary>
    private sealed class RecordingNoticeStore : INoticeStore
    {
        public List<NoticeItem> Items { get; } = [];

#pragma warning disable CS0067 // 测试替身：变化事件不触发
        public event EventHandler<NoticeItem>? Changed;
#pragma warning restore CS0067

        public Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null,
            string? groupOpenId = null, DateTimeOffset? createdAt = null, CancellationToken ct = default)
        {
            var existing = Items.Find(i => i.MessageId == messageId);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var item = new NoticeItem
            {
                MessageId = messageId,
                Content = content,
                MemberOpenId = memberOpenId ?? "",
                GroupOpenId = groupOpenId ?? "",
                CreatedAt = createdAt ?? DateTimeOffset.Now
            };
            Items.Add(item);
            return Task.FromResult(item);
        }

        public Task MarkReadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkUnreadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>(Items.Where(i => !i.IsRead).ToList());

        public Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>(Items.ToList());

        public Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>(
                Items.Where(i => RetentionPolicies.BucketOf(i.CreatedAt) == date).ToList());

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> RemoveByMessageIdAsync(string messageId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    /// <summary>记录落档时间与文档条目的作业存储替身。</summary>
    private sealed class RecordingHomeworkStore : IHomeworkStore
    {
        public List<HomeworkItem> Items { get; } = [];

        public List<(string Subject, HomeworkDocumentEntry Entry)> DocumentAppendCalls { get; } = [];

#pragma warning disable CS0067 // 测试替身：变化事件不触发
        public event EventHandler<HomeworkItem>? Changed;
        public event EventHandler<HomeworkDocument>? DocumentChanged;
#pragma warning restore CS0067

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
        {
            var existing = Items.Find(i => i.MessageId == item.MessageId);
            if (existing is null)
            {
                Items.Add(item);
                return Task.FromResult(item);
            }

            // 幂等语义与真实 HomeworkStore 一致：保留原 CreatedAt
            existing.Subject = item.Subject;
            existing.SubjectConfidence = item.SubjectConfidence;
            existing.SubjectSource = item.SubjectSource;
            existing.AttachmentIds = item.AttachmentIds;
            return Task.FromResult(existing);
        }

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.Where(i => i.Subject == subject).ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(
                Items.Where(i => RetentionPolicies.BucketOf(i.CreatedAt) == date).ToList());

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<HomeworkDocument>> GetDocumentsAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkDocument>>([]);

        public Task<HomeworkDocument> AppendDocumentEntryAsync(
            string subject, HomeworkDocumentEntry entry, CancellationToken ct = default)
        {
            DocumentAppendCalls.Add((subject, entry));
            return Task.FromResult(new HomeworkDocument
            {
                Date = RetentionPolicies.BucketOf(entry.CreatedAt),
                Subject = subject,
                Entries = [entry],
                UpdatedAt = DateTimeOffset.Now
            });
        }

        public Task<HomeworkDocument?> SaveDocumentTextAsync(
            DateOnly date, string subject, string? manualText, CancellationToken ct = default,
            IReadOnlyCollection<Guid>? knownEntryIds = null)
            => Task.FromResult<HomeworkDocument?>(null);

        public Task<int> RemoveByMessageIdAsync(string messageId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RemoveDocumentAsync(DateOnly date, string subject, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    /// <summary>记录附件入队时间的文件管道替身（含带时间的新重载）。</summary>
    private sealed class RecordingFilePipeline : IFilePipelineService
    {
        public List<DateTimeOffset?> EnqueuedCreatedAts { get; } = [];

        public List<FileRecord> Records { get; } = [];

#pragma warning disable CS0067 // 测试替身：变化事件不触发
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
            => Enqueue(messageId, fileName, url, null);

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId, string? groupOpenId, DateTimeOffset? createdAt, CancellationToken ct = default)
            => Enqueue(messageId, fileName, url, createdAt);

        private Task<FileRecord> Enqueue(string messageId, string fileName, string? url, DateTimeOffset? createdAt)
        {
            EnqueuedCreatedAts.Add(createdAt);
            var record = new FileRecord
            {
                MessageId = messageId,
                FileName = fileName,
                Status = FileStatus.Archived,
                CreatedAt = createdAt ?? DateTimeOffset.Now
            };
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileRecord>>(Records.ToList());
    }

    /// <summary>只实现旧签名的文件管道替身：验证新增重载的接口默认实现向后兼容。</summary>
    private sealed class LegacyOnlyFilePipeline : IFilePipelineService
    {
#pragma warning disable CS0067 // 测试替身：变化事件不触发
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
            => Task.FromResult(new FileRecord
            {
                MessageId = messageId,
                FileName = fileName,
                Status = FileStatus.Failed,
                CreatedAt = DateTimeOffset.Now
            });

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileRecord>>([]);
    }
}
