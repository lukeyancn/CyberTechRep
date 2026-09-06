using System.Runtime.Versioning;
using ClassIng.Plugin.Services.Maintenance;
using ClassIng.Plugin.Services.Pipeline;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>
/// 集成收口：消息主数据流端到端测试。
/// 注入 fake 消息 → 断言通知/作业/文件记录产生，以及重试执行器与人工确认写回接线。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MessageDispatchServiceTests : IDisposable
{
    private readonly string _dir;

    public MessageDispatchServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "dispatch", Guid.NewGuid().ToString("N"));
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
        public int StartedCount { get; private set; }

        public ConnectionStatus Status => ConnectionStatus.Disconnected;

        public event EventHandler<MessageRecord>? MessageReceived;

#pragma warning disable CS0067 // 测试替身：连接状态变化事件不触发
        public event EventHandler<ConnectionStatus>? StatusChanged;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default)
        {
            StartedCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> FetchHistoryAsync(int days, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task ReconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(MessageRecord message) => MessageReceived?.Invoke(this, message);
    }

    private sealed class FakeClassifier : IMessageClassifier
    {
        public Func<MessageRecord, ClassifiedMessage>? Handler { get; set; }

        public int CallCount { get; private set; }

        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default)
        {
            CallCount++;
            if (Handler is not null)
            {
                return Task.FromResult(Handler(message));
            }

            throw new InvalidOperationException("分类器故障");
        }

        public void ReloadRules()
        {
        }
    }

    private sealed class FakeSubjectChain : ISubjectClassifierChain
    {
        public Func<string, string, SubjectResult>? Handler { get; set; }

        public int CallCount { get; private set; }

        public Task<SubjectResult> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Handler is not null
                ? Handler(text, messageId)
                : new SubjectResult { Subject = "数学", Confidence = 1.0, Source = SubjectSource.KeywordRule });
        }
    }

    private sealed class FakeNoticeStore : INoticeStore
    {
        public List<NoticeItem> Items { get; } = [];

        /// <summary>每次 AddOrUpdateAsync 收到的 memberOpenId（验证发送者链路接线）。</summary>
        public List<string?> MemberOpenIds { get; } = [];

        public Func<string, string, Task>? OnAddOrUpdate { get; set; }

        public event EventHandler<NoticeItem>? Changed;

        public async Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null, CancellationToken ct = default)
        {
            if (OnAddOrUpdate is not null)
            {
                await OnAddOrUpdate(messageId, content);
            }

            MemberOpenIds.Add(memberOpenId);
            var existing = Items.Find(i => i.MessageId == messageId);
            if (existing is not null)
            {
                var updated = new NoticeItem
                {
                    Id = existing.Id,
                    MessageId = messageId,
                    Content = content,
                    CreatedAt = existing.CreatedAt,
                    IsRead = existing.IsRead,
                    ReadAt = existing.ReadAt
                };
                Items[Items.IndexOf(existing)] = updated;
                Changed?.Invoke(this, updated);
                return updated;
            }

            var item = new NoticeItem { MessageId = messageId, Content = content };
            Items.Add(item);
            Changed?.Invoke(this, item);
            return item;
        }

        public Task MarkReadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkUnreadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>(Items.Where(i => !i.IsRead).ToList());

        public Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>(Items.ToList());

        public Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>(
                Items.Where(i => DateOnly.FromDateTime(i.CreatedAt.LocalDateTime) == date).ToList());

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeHomeworkStore : IHomeworkStore
    {
        public List<HomeworkItem> Items { get; } = [];

        public List<(Guid Id, string Subject)> SetSubjectCalls { get; } = [];

        public event EventHandler<HomeworkItem>? Changed;

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
        {
            var existing = Items.Find(i => i.Id == item.Id)
                           ?? Items.Find(i => i.MessageId == item.MessageId);
            if (existing is null)
            {
                Items.Add(item);
                Changed?.Invoke(this, item);
                return Task.FromResult(item);
            }

            existing.Subject = item.Subject;
            existing.SubjectConfidence = item.SubjectConfidence;
            existing.SubjectSource = item.SubjectSource;
            existing.AttachmentIds = item.AttachmentIds;
            Changed?.Invoke(this, existing);
            return Task.FromResult(existing);
        }

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default)
        {
            SetSubjectCalls.Add((id, subject));
            var item = Items.Find(i => i.Id == id);
            if (item is not null)
            {
                item.Subject = subject;
                item.SubjectSource = SubjectSource.Manual;
                Changed?.Invoke(this, item);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.Where(i => i.Subject == subject).ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(
                Items.Where(i => DateOnly.FromDateTime(i.CreatedAt.LocalDateTime) == date).ToList());

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeFilePipeline : IFilePipelineService
    {
        public List<(string MessageId, string FileName, string? Url)> EnqueueCalls { get; } = [];

        public List<(Guid FileId, string Subject)> ReassignCalls { get; } = [];

        public List<FileRecord> Records { get; } = [];

        public event EventHandler<FileRecord>? FileUpdated;

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url, CancellationToken ct = default)
        {
            EnqueueCalls.Add((messageId, fileName, url));
            var record = new FileRecord
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                FileName = fileName,
                Status = FileStatus.Archived,
                CompletedAt = DateTimeOffset.Now
            };
            Records.Add(record);
            FileUpdated?.Invoke(this, record);
            return Task.FromResult(record);
        }

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
        {
            ReassignCalls.Add((fileId, subject));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileRecord>>(Records.ToList());
    }

    private sealed class FakeUpdateNotify : IUpdateNotifyService
    {
        public event EventHandler<UpdateInfo>? UpdateDetected;

        public Task CheckAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Raise(UpdateInfo info) => UpdateDetected?.Invoke(this, info);
    }

    // ============ 构造辅助 ============

    private static MessageRecord Message(string id, params MessageSegment[] segments) => new()
    {
        MessageId = id,
        GroupOpenId = "group-1",
        ReceivedAt = DateTimeOffset.Now,
        Segments = segments
    };

    private static MessageSegment Text(string text) => new() { Type = SegmentTypes.Text, Text = text };

    private RetryQueueService CreateRetryQueue() => new(new RetryQueueOptions
    {
        GetSettings = () => new MaintenanceSettings(),
        DataDirectory = _dir,
        AutoStartScheduler = false
    });

    private MessageDispatchService CreateDispatch(
        FakeIngestService? ingest = null,
        FakeClassifier? classifier = null,
        FakeSubjectChain? chain = null,
        FakeNoticeStore? notices = null,
        FakeHomeworkStore? homework = null,
        FakeFilePipeline? files = null,
        RetryQueueService? retryQueue = null,
        FakeUpdateNotify? update = null,
        JsonPendingConfirmStore? pending = null,
        INoticeStore? noticesOverride = null)
    {
        return new MessageDispatchService(
            ingest ?? new FakeIngestService(),
            classifier ?? new FakeClassifier(),
            chain ?? new FakeSubjectChain(),
            (INoticeStore?)noticesOverride ?? notices ?? new FakeNoticeStore(),
            homework ?? new FakeHomeworkStore(),
            files ?? new FakeFilePipeline(),
            retryQueue,
            update,
            pending);
    }

    // ============ 用例 ============

    [Fact]
    public async Task NoticeMessage_ClassifiedThenWrittenToNoticeStore()
    {
        var notices = new FakeNoticeStore();
        var classifier = new FakeClassifier
        {
            Handler = m => new ClassifiedMessage
            {
                Source = m, Kind = MessageKind.Notice, Confidence = 1.0, MatchReason = "notice_hit=[通知]"
            }
        };
        var dispatch = CreateDispatch(classifier: classifier, notices: notices);

        await dispatch.ProcessMessageAsync(Message("m1", Text("停课通知：明天放假")));

        var item = Assert.Single(notices.Items);
        Assert.Equal("m1", item.MessageId);
        Assert.Equal("停课通知：明天放假", item.Content);
        Assert.False(item.IsRead);
    }

    [Fact]
    public async Task HomeworkMessage_FullFlow_UpsertsWithSubjectAndAttachmentsAndReassignsFiles()
    {
        var classifier = new FakeClassifier
        {
            Handler = m => new ClassifiedMessage
            {
                Source = m, Kind = MessageKind.Homework, Confidence = 1.0, MatchReason = "homework_hit=[作业]"
            }
        };
        var chain = new FakeSubjectChain
        {
            Handler = (_, _) => new SubjectResult
            {
                Subject = "数学", Confidence = 0.95, Source = SubjectSource.KeywordRule
            }
        };
        var homework = new FakeHomeworkStore();
        var files = new FakeFilePipeline();
        var dispatch = CreateDispatch(classifier: classifier, chain: chain, homework: homework, files: files);

        var message = Message("m2",
            Text("今天的作业：口算一页"),
            new MessageSegment { Type = SegmentTypes.File, Url = "https://example.com/wb.pdf", FileName = "wb.pdf" });
        await dispatch.ProcessMessageAsync(message);

        // 作业条目产生且字段正确
        var item = Assert.Single(homework.Items);
        Assert.Equal("m2", item.MessageId);
        Assert.Equal("数学", item.Subject);
        Assert.Equal(0.95, item.SubjectConfidence);
        Assert.Equal(SubjectSource.KeywordRule, item.SubjectSource);
        Assert.Equal("今天的作业：口算一页", item.Content);

        // 文件段进入文件管道，且附件 Id 关联到作业条目
        var (msgId, fileName, url) = Assert.Single(files.EnqueueCalls);
        Assert.Equal("m2", msgId);
        Assert.Equal("wb.pdf", fileName);
        Assert.Equal("https://example.com/wb.pdf", url);
        Assert.Contains(files.ReassignCalls, c => c.FileId == item.AttachmentIds.Single() && c.Subject == "数学");
    }

    [Fact]
    public async Task NoticeMessage_WithImageVideoFileSegments_EnqueuesAllToPipeline()
    {
        var classifier = new FakeClassifier
        {
            Handler = m => new ClassifiedMessage
            {
                Source = m, Kind = MessageKind.Notice, Confidence = 1.0, MatchReason = "notice_hit"
            }
        };
        var files = new FakeFilePipeline();
        var dispatch = CreateDispatch(classifier: classifier, files: files);

        await dispatch.ProcessMessageAsync(Message("m3",
            Text("运动会照片通知"),
            new MessageSegment { Type = SegmentTypes.Image, Url = "https://example.com/a.jpg" },
            new MessageSegment { Type = SegmentTypes.Video, Url = "https://example.com/b.mp4" },
            new MessageSegment { Type = SegmentTypes.File, Url = "https://example.com/c.docx", FileName = "安排.docx" }));

        Assert.Equal(3, files.EnqueueCalls.Count);
        Assert.Equal("安排.docx", files.EnqueueCalls[2].FileName);
    }

    [Fact]
    public async Task ClassifierThrows_MessageSkipped_NoStoreWrites_NoCrash()
    {
        var classifier = new FakeClassifier(); // Handler 为 null → 抛异常
        var notices = new FakeNoticeStore();
        var homework = new FakeHomeworkStore();
        var dispatch = CreateDispatch(classifier: classifier, notices: notices, homework: homework);

        await dispatch.ProcessMessageAsync(Message("m4", Text("任意内容")));

        Assert.Empty(notices.Items);
        Assert.Empty(homework.Items);
    }

    [Fact]
    public async Task NoticeStoreWriteFails_FailureEnqueuedToRetryQueue()
    {
        var notices = new FakeNoticeStore
        {
            OnAddOrUpdate = (_, _) => throw new IOException("磁盘写入失败")
        };
        var classifier = new FakeClassifier
        {
            Handler = m => new ClassifiedMessage
            {
                Source = m, Kind = MessageKind.Notice, Confidence = 1.0, MatchReason = "notice_hit"
            }
        };
        var retry = CreateRetryQueue();
        var dispatch = CreateDispatch(classifier: classifier, notices: notices, retryQueue: retry);

        await dispatch.ProcessMessageAsync(Message("m5", Text("通知内容")));

        var items = await retry.GetAllAsync();
        var item = Assert.Single(items);
        Assert.Equal(RetryOperationType.StoreWrite, item.OperationType);
        Assert.Equal(RetryItemStatus.Waiting, item.Status);
    }

    [Fact]
    public async Task FileDownloadRetryExecutor_ReenqueuesToPipeline()
    {
        var retry = CreateRetryQueue();
        var files = new FakeFilePipeline();
        var dispatch = CreateDispatch(files: files, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        await retry.EnqueueAsync(
            RetryOperationType.FileDownload,
            MessageDispatchService.BuildFileDownloadPayload("m6", "workbook.pdf", "https://example.com/wb.pdf"));
        await retry.TickAsync(forceDue: true);

        var (msgId, fileName, url) = Assert.Single(files.EnqueueCalls);
        Assert.Equal("m6", msgId);
        Assert.Equal("workbook.pdf", fileName);
        Assert.Equal("https://example.com/wb.pdf", url);

        var items = await retry.GetAllAsync();
        Assert.Equal(RetryItemStatus.Succeeded, items[0].Status);
        dispatch.Dispose();
    }

    [Fact]
    public async Task SubjectClassifyRetryExecutor_ReClassifiesAndUpsertsHomework()
    {
        var retry = CreateRetryQueue();
        var chain = new FakeSubjectChain
        {
            Handler = (_, _) => new SubjectResult
            {
                Subject = "英语", Confidence = 0.9, Source = SubjectSource.CloudLlm
            }
        };
        var homework = new FakeHomeworkStore();
        var dispatch = CreateDispatch(chain: chain, homework: homework, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        await retry.EnqueueAsync(
            RetryOperationType.SubjectClassify,
            MessageDispatchService.BuildSubjectClassifyPayload("m7", "背单词二十个"));
        await retry.TickAsync(forceDue: true);

        Assert.Equal(1, chain.CallCount);
        var item = Assert.Single(homework.Items);
        Assert.Equal("m7", item.MessageId);
        Assert.Equal("英语", item.Subject);
        Assert.Equal(SubjectSource.CloudLlm, item.SubjectSource);

        var items = await retry.GetAllAsync();
        Assert.Equal(RetryItemStatus.Succeeded, items[0].Status);
        dispatch.Dispose();
    }

    [Fact]
    public async Task StoreWriteRetryExecutor_ReplaysNoticeWrite()
    {
        var retry = CreateRetryQueue();
        var notices = new FakeNoticeStore();
        var dispatch = CreateDispatch(notices: notices, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        var payload = new MessageDispatchService.StoreWritePayload(
            MessageDispatchService.StoreWriteKind.NoticeUpsert, "m8", "重放的通知", null);
        await retry.EnqueueAsync(RetryOperationType.StoreWrite, payload);
        await retry.TickAsync(forceDue: true);

        var stored = Assert.Single(notices.Items);
        Assert.Equal("m8", stored.MessageId);
        Assert.Equal("重放的通知", stored.Content);
        dispatch.Dispose();
    }

    [Fact]
    public async Task StoreWriteRetryExecutor_ReplaysNoticeWrite_WithMemberOpenId()
    {
        var retry = CreateRetryQueue();
        var notices = new FakeNoticeStore();
        var dispatch = CreateDispatch(notices: notices, retryQueue: retry);
        await dispatch.StartAsync(CancellationToken.None);

        var payload = new MessageDispatchService.StoreWritePayload(
            MessageDispatchService.StoreWriteKind.NoticeUpsert, "m9", "重放的通知", null, MemberOpenId: "u1");
        await retry.EnqueueAsync(RetryOperationType.StoreWrite, payload);
        await retry.TickAsync(forceDue: true);

        Assert.Single(notices.Items);
        Assert.Equal("u1", Assert.Single(notices.MemberOpenIds));
        dispatch.Dispose();
    }

    [Fact]
    public async Task StoreWriteRetryExecutor_ReplaysMappedSenderNotice_WithSubjectPrefix()
    {
        // 端到端：StoreWrite 重放链路也套用「发送者→学科」前缀（真实 NoticeStore）
        var rules = new UserSubjectRuleStore(_dir);
        rules.Set("u1", "英语");
        var notices = new NoticeStore(_dir, userRules: rules);
        var retry = CreateRetryQueue();
        var dispatch = CreateDispatch(retryQueue: retry, noticesOverride: notices);
        await dispatch.StartAsync(CancellationToken.None);

        var payload = new MessageDispatchService.StoreWritePayload(
            MessageDispatchService.StoreWriteKind.NoticeUpsert, "m10", "明天交作业", null, MemberOpenId: "u1");
        await retry.EnqueueAsync(RetryOperationType.StoreWrite, payload);
        await retry.TickAsync(forceDue: true);

        var stored = Assert.Single(await notices.GetAllAsync());
        Assert.Equal("英语：明天交作业", stored.Content);
        dispatch.Dispose();
    }

    [Fact]
    public async Task UpdateDetected_WritesNoticeEntry()
    {
        var notices = new FakeNoticeStore();
        var update = new FakeUpdateNotify();
        var dispatch = CreateDispatch(notices: notices, update: update);
        await dispatch.StartAsync(CancellationToken.None);

        update.Raise(new UpdateInfo("1.2.3", "1.2.2", "https://github.com/example/releases", "修复若干问题"));

        var item = Assert.Single(notices.Items);
        Assert.Equal("update:1.2.3", item.MessageId);
        Assert.Contains("1.2.3", item.Content);
        Assert.Contains("修复若干问题", item.Content);
        dispatch.Dispose();
    }

    [Fact]
    public async Task PendingConfirmResolved_WritesBackHomeworkAndReassignsFiles()
    {
        var pending = new JsonPendingConfirmStore(new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            DataDirectory = _dir
        });
        var homework = new FakeHomeworkStore();
        var files = new FakeFilePipeline();
        var dispatch = CreateDispatch(homework: homework, files: files, pending: pending);
        await dispatch.StartAsync(CancellationToken.None);

        // 先产生一条作业与附件记录（模拟主流程已写入）
        homework.Items.Add(new HomeworkItem
        {
            MessageId = "m9",
            Content = "低置信作业",
            Subject = "未分类",
            CreatedAt = DateTimeOffset.Now
        });
        await files.EnqueueAsync("m9", "sheet.pdf", "https://example.com/s.pdf");

        // 人工确认Resolve → 写回 HomeworkStore + 文件二次归档
        var item = await pending.EnqueueAsync("m9", "低置信作业", []);
        await pending.ResolveAsync(item.Id, "物理");

        var call = Assert.Single(homework.SetSubjectCalls);
        Assert.Equal("物理", call.Subject);
        Assert.Equal(homework.Items[0].Id, call.Id);
        Assert.Contains(files.ReassignCalls, c => c.Subject == "物理");
        dispatch.Dispose();
    }

    [Fact]
    public async Task StartAsync_StartsIngestGateway()
    {
        var ingest = new FakeIngestService();
        var dispatch = CreateDispatch(ingest: ingest);
        await dispatch.StartAsync(CancellationToken.None);
        await dispatch.StopAsync(CancellationToken.None);

        Assert.Equal(1, ingest.StartedCount);
        dispatch.Dispose();
    }
}
