using System.Net;
using System.Runtime.Versioning;
using System.Text;
using ClassIng.Plugin.Services.Files;
using ClassIng.Plugin.Services.Pipeline;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>
/// 需求（绑定生效）：成员学科绑定回溯。
/// 收口点 = MemberSubjectBindingStore.Set 的 BindingSet 事件；
/// 通知回溯（未分类补记、已有学科不覆盖、群作用域回落）+ 文件回溯（未分类重归档、已归档不动）。
/// </summary>
public sealed class MemberBindingBackfillTests : IDisposable
{
    private readonly string _dir;

    public MemberBindingBackfillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "binding-backfill", Guid.NewGuid().ToString("N"));
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

    private MemberSubjectBindingStore CreateBindings() => new(_dir);

    private NoticeStore CreateNoticeStore(MemberSubjectBindingStore bindings) =>
        new(_dir, memberBindingSubject: (m, g) => bindings.GetSubject(m, g));

    /// <summary>归档相对路径首段（学科目录名；Windows/POSIX 分隔符兼容）。</summary>
    private static string FirstSegment(string? relativePath) =>
        (relativePath ?? "").Split('/', '\\')[0];

    // ============ 收口点：BindingSet 事件 ============

    [Fact]
    public void Set_RaisesBindingSetEvent_OncePerWrite()
    {
        var bindings = CreateBindings();
        var events = new List<MemberBindingChangedEventArgs>();
        bindings.BindingSet += (_, e) => events.Add(e);

        bindings.Set("member-1", "物理");
        bindings.Set("member-1", "物理", "group-a");

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.MemberOpenId == "member-1" && e.GroupOpenId.Length == 0 && e.Subject == "物理");
        Assert.Contains(events, e => e.MemberOpenId == "member-1" && e.GroupOpenId == "group-a" && e.Subject == "物理");
    }

    [Fact]
    public void Remove_DoesNotRaiseBindingSetEvent()
    {
        var bindings = CreateBindings();
        bindings.Set("member-1", "物理");
        var count = 0;
        bindings.BindingSet += (_, _) => count++;

        bindings.Remove("member-1");

        Assert.Equal(0, count);
    }

    // ============ 通知回溯（NoticeStore.BackfillMemberSubjectAsync） ============

    [Fact]
    public async Task NoticeBackfill_UnclassifiedNotices_GetBindingPrefixAndPersist()
    {
        var bindings = CreateBindings();
        var store = CreateNoticeStore(bindings);
        var changed = new List<NoticeItem>();
        store.Changed += (_, item) => changed.Add(item);

        // 绑定前写入：无前缀、无学科（未分类）
        await store.AddOrUpdateAsync("m1", "明天早自习提前十分钟", "member-1", "group-1");
        Assert.Equal("明天早自习提前十分钟", (await store.GetAllAsync())[0].Content);
        changed.Clear(); // 仅统计回溯阶段触发的 Changed

        bindings.Set("member-1", "物理");
        var count = await store.BackfillMemberSubjectAsync("member-1");

        Assert.Equal(1, count);
        var item = (await store.GetAllAsync()).Single(i => i.MessageId == "m1");
        Assert.Equal("物理：明天早自习提前十分钟", item.Content);
        Assert.Equal("物理", item.Subject);

        // 回溯广播 Changed 刷新悬浮窗
        Assert.Single(changed, i => i.Id == item.Id);

        // 持久化：新实例加载后仍是回溯结果
        var reloaded = await CreateNoticeStore(bindings).GetAllAsync();
        Assert.Equal("物理：明天早自习提前十分钟", reloaded.Single(i => i.MessageId == "m1").Content);
    }

    [Fact]
    public async Task NoticeBackfill_NoticeWithSubject_NotOverwritten()
    {
        var bindings = CreateBindings();
        var store = CreateNoticeStore(bindings);

        // 已有学科（此前绑定/学习/人工结果语义）的通知绝不覆盖（红线）
        bindings.Set("member-1", "物理");
        await store.AddOrUpdateAsync("m1", "已定学科通知", "member-1", "group-1");
        bindings.Set("member-1", "化学");

        var count = await store.BackfillMemberSubjectAsync("member-1");

        Assert.Equal(0, count);
        var item = (await store.GetAllAsync()).Single(i => i.MessageId == "m1");
        Assert.Equal("物理：已定学科通知", item.Content);
        Assert.Equal("物理", item.Subject);
    }

    [Fact]
    public async Task NoticeBackfill_GroupScopedBinding_ResolvedPerNoticeGroup()
    {
        var bindings = CreateBindings();
        var store = CreateNoticeStore(bindings);

        // 两条未分类通知分别来自 g1 / g2（绑定前写入）
        await store.AddOrUpdateAsync("n-g1", "g1 的通知", "member-1", "g1");
        await store.AddOrUpdateAsync("n-g2", "g2 的通知", "member-1", "g2");

        // 全局绑定 → 回溯时两条都补全局学科
        bindings.Set("member-1", "数学");
        await store.BackfillMemberSubjectAsync("member-1");
        Assert.Equal("数学：g1 的通知", (await store.GetAllAsync()).Single(i => i.MessageId == "n-g1").Content);
        Assert.Equal("数学：g2 的通知", (await store.GetAllAsync()).Single(i => i.MessageId == "n-g2").Content);

        // g1 群作用域覆盖后：新未分类通知（g1）按群绑定补记；g2 既有学科不动
        bindings.Set("member-1", "物理", "g1");
        await store.AddOrUpdateAsync("n-g3", "g1 的新通知", "member-1", "g1");
        await store.BackfillMemberSubjectAsync("member-1");

        Assert.Equal("物理：g1 的新通知", (await store.GetAllAsync()).Single(i => i.MessageId == "n-g3").Content);
        Assert.Equal("数学：g1 的通知", (await store.GetAllAsync()).Single(i => i.MessageId == "n-g1").Content);
    }

    [Fact]
    public async Task NoticeBackfill_NoBindingOrOtherMember_NoChange()
    {
        var bindings = CreateBindings();
        var store = CreateNoticeStore(bindings);
        await store.AddOrUpdateAsync("m1", "无绑定通知", "member-1", "group-1");
        await store.AddOrUpdateAsync("m2", "其他成员通知", "member-2", "group-1");

        // 未绑定 → 0 条，内容原样
        Assert.Equal(0, await store.BackfillMemberSubjectAsync("member-1"));
        Assert.Equal("无绑定通知", (await store.GetAllAsync()).Single(i => i.MessageId == "m1").Content);

        // 绑定 member-2 后回溯 member-1 → member-2 的通知不受影响，member-1 仍无学科
        bindings.Set("member-2", "化学");
        Assert.Equal(0, await store.BackfillMemberSubjectAsync("member-1"));
        Assert.Equal("无绑定通知", (await store.GetAllAsync()).Single(i => i.MessageId == "m1").Content);
        Assert.Equal("", (await store.GetAllAsync()).Single(i => i.MessageId == "m1").Subject);
    }

    [Fact]
    public async Task NoticeWrite_AfterBinding_AppliesPrefixAndRecordsSubject()
    {
        // 后续通知走绑定：写入时绑定生效 → 前缀 + Subject 同步记录
        var bindings = CreateBindings();
        bindings.Set("member-1", "物理", "group-1");
        var store = CreateNoticeStore(bindings);

        await store.AddOrUpdateAsync("m1", "后续通知", "member-1", "group-1");
        var item = (await store.GetAllAsync()).Single();
        Assert.Equal("物理：后续通知", item.Content);
        Assert.Equal("物理", item.Subject);
    }

    // ============ 文件回溯（真实 FilePipelineService + 回溯器） ============

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpContent> _responses = new(StringComparer.Ordinal);

        public void Map(string url, byte[] content) => _responses[url] = new ByteArrayContent(content);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(_responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private FilePipelineService CreateFilePipeline(FakeHandler handler, FileSettings? settings = null)
    {
        settings ??= new FileSettings();
        var provider = new FilePipelineOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = _dir
        };
        return new FilePipelineService(provider, new HttpClient(handler));
    }

    private MemberBindingBackfillService CreateBackfill(
        MemberSubjectBindingStore bindings, FilePipelineService files,
        HomeworkStore? homework = null, NoticeStore? notices = null) =>
        new(bindings, notices, homework, files);

    [Fact]
    public async Task FileBackfill_UnclassifiedRecord_RearchivedByBinding()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/a.pdf", Encoding.UTF8.GetBytes("file-a"));
        var files = CreateFilePipeline(handler);
        var bindings = CreateBindings();
        var backfill = CreateBackfill(bindings, files);

        // 绑定前：文件归档到 未分类/<日期>/（记录携带发送者归因）
        var record = await files.EnqueueAsync("msg-1", "a.pdf", "https://example.com/a.pdf", "member-1", "group-1");
        Assert.Equal(FileStatus.Archived, record.Status);
        Assert.Equal(HomeworkSubjectResolver.Unclassified, FirstSegment(record.ArchivedRelativePath));

        // 绑定写入（不经 Attach 直接驱动回溯器，保证确定性）
        bindings.Set("member-1", "物理");
        await backfill.RunBackfillAsync("member-1", "", "物理");

        var after = (await files.GetRecordsAsync()).Single(r => r.Id == record.Id);
        Assert.Equal("物理", FirstSegment(after.ArchivedRelativePath));
        // 日期目录保留：物理/<yyyy-MM-dd>/a.pdf
        var segments = after.ArchivedRelativePath!.Split('/', '\\');
        Assert.Equal(3, segments.Length);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"), segments[1]);
        Assert.True(File.Exists(Path.Combine(_dir, "下载文件", after.ArchivedRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task FileBackfill_RecordAlreadyInOtherSubject_Unmoved()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/b.pdf", Encoding.UTF8.GetBytes("file-b"));
        var files = CreateFilePipeline(handler);
        var bindings = CreateBindings();
        var backfill = CreateBackfill(bindings, files);

        var record = await files.EnqueueAsync("msg-1", "b.pdf", "https://example.com/b.pdf", "member-1", "group-1");
        // 已归档到其他学科（此前链结果/人工修正语义）
        await files.ReassignSubjectAsync(record.Id, "数学");
        bindings.Set("member-1", "物理");

        await backfill.RunBackfillAsync("member-1", "", "物理");

        var after = (await files.GetRecordsAsync()).Single(r => r.Id == record.Id);
        Assert.Equal("数学", FirstSegment(after.ArchivedRelativePath));
    }

    [Fact]
    public async Task FileBackfill_LegacyRecordWithoutSender_AttributedViaStores()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/c.pdf", Encoding.UTF8.GetBytes("file-c"));
        var files = CreateFilePipeline(handler);
        var bindings = CreateBindings();
        var homework = new HomeworkStore(_dir);
        var notices = CreateNoticeStore(bindings);
        var backfill = new MemberBindingBackfillService(bindings, notices, homework, files);

        // 旧记录：无发送者归因（MemberOpenId 空），经作业存储 MessageId→成员映射兜底
        var record = await files.EnqueueAsync("msg-old", "c.pdf", "https://example.com/c.pdf");
        Assert.Equal("", record.MemberOpenId);
        await homework.UpsertAsync(new HomeworkItem
        {
            MessageId = "msg-old",
            MemberOpenId = "member-1",
            Content = "旧作业"
        });
        bindings.Set("member-1", "英语");

        await backfill.RunBackfillAsync("member-1", "", "英语");

        var after = (await files.GetRecordsAsync()).Single(r => r.Id == record.Id);
        Assert.Equal("英语", FirstSegment(after.ArchivedRelativePath));
    }

    [Fact]
    public async Task FileBackfill_GroupScopeBinding_ResolvesPerRecordGroup()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/d1.pdf", Encoding.UTF8.GetBytes("d1"));
        handler.Map("https://example.com/d2.pdf", Encoding.UTF8.GetBytes("d2"));
        var files = CreateFilePipeline(handler);
        var bindings = CreateBindings();
        var notices = CreateNoticeStore(bindings);
        var backfill = new MemberBindingBackfillService(bindings, notices, null, files);

        // 两条旧记录：归因来自通知存储（含群作用域）
        var r1 = await files.EnqueueAsync("msg-g1", "d1.pdf", "https://example.com/d1.pdf");
        var r2 = await files.EnqueueAsync("msg-g2", "d2.pdf", "https://example.com/d2.pdf");
        await notices.AddOrUpdateAsync("msg-g1", "g1 通知", "member-1", "g1");
        await notices.AddOrUpdateAsync("msg-g2", "g2 通知", "member-1", "g2");

        bindings.Set("member-1", "数学"); // 全局
        bindings.Set("member-1", "物理", "g1"); // g1 覆盖

        await backfill.RunBackfillAsync("member-1", "g1", "物理");

        Assert.Equal("物理", FirstSegment((await files.GetRecordsAsync()).Single(r => r.Id == r1.Id).ArchivedRelativePath));
        Assert.Equal("数学", FirstSegment((await files.GetRecordsAsync()).Single(r => r.Id == r2.Id).ArchivedRelativePath));
    }

    [Fact]
    public async Task FileBackfill_NonArchivedRecord_Skipped()
    {
        var files = CreateFilePipeline(new FakeHandler()); // 无映射 URL → 下载失败
        var bindings = CreateBindings();
        var backfill = CreateBackfill(bindings, files);

        var record = await files.EnqueueAsync("msg-1", "bad.pdf", "https://example.com/missing.pdf", "member-1", "g1");
        Assert.Equal(FileStatus.Failed, record.Status);

        bindings.Set("member-1", "物理");
        await backfill.RunBackfillAsync("member-1", "", "物理"); // 不抛

        Assert.Equal(FileStatus.Failed, (await files.GetRecordsAsync()).Single(r => r.Id == record.Id).Status);
    }

    /// <summary>文件记录读取抛异常的替身：回溯全程不外抛（不阻塞主流程红线）。</summary>
    private sealed class ThrowingFilePipeline : IFilePipelineService
    {
#pragma warning disable CS0067
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task RunBackfill_AllDependenciesFaultOrNull_DoesNotThrow()
    {
        // 全空依赖
        var empty = new MemberBindingBackfillService();
        await empty.RunBackfillAsync("member-1", "", "物理");

        // 文件管道抛异常 + 通知存储正常 → 不外抛
        var bindings = CreateBindings();
        var notices = CreateNoticeStore(bindings);
        var service = new MemberBindingBackfillService(bindings, notices, null, new ThrowingFilePipeline());
        bindings.Set("member-1", "物理");
        await service.RunBackfillAsync("member-1", "", "物理");

        // 未绑定成员回溯 → 直接完成
        await service.RunBackfillAsync("member-unknown", "", "物理");
    }

    [Fact]
    public async Task Attach_OnBindingSet_BackfillRunsAndReassignsFile()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/e.pdf", Encoding.UTF8.GetBytes("file-e"));
        var files = CreateFilePipeline(handler);
        var bindings = CreateBindings();
        var backfill = CreateBackfill(bindings, files);

        var record = await files.EnqueueAsync("msg-1", "e.pdf", "https://example.com/e.pdf", "member-1", "group-1");

        // 接线（模拟宿主 StartAsync）：Set 触发 fire-and-forget 回溯，经 FileUpdated 事件等待完成
        backfill.Attach();
        var reassigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.FileUpdated += (_, r) =>
        {
            if (r.Id == record.Id && FirstSegment(r.ArchivedRelativePath) == "物理")
            {
                reassigned.TrySetResult();
            }
        };

        bindings.Set("member-1", "物理");
        var finished = await Task.WhenAny(reassigned.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        backfill.Detach();
        Assert.Equal(reassigned.Task, finished);
        Assert.Equal("物理", FirstSegment((await files.GetRecordsAsync()).Single(r => r.Id == record.Id).ArchivedRelativePath));
    }
}

/// <summary>
/// 需求（后续文件走绑定）：MessageDispatchService 归档定学科时成员显式绑定纳入学科来源——
/// 通知/未分类消息的附件按绑定二次归档；Keyword 模式不读取绑定（现状不变）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FutureFileBindingRoutingTests
{
    private sealed class FakeNoticeClassifier : IMessageClassifier
    {
        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default) =>
            Task.FromResult(new ClassifiedMessage
            {
                Source = message, Kind = MessageKind.Notice, Confidence = 1.0, MatchReason = "notice-keyword"
            });

        public void ReloadRules()
        {
        }
    }

    private sealed class FakeUnknownClassifier : IMessageClassifier
    {
        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default) =>
            Task.FromResult(new ClassifiedMessage
            {
                Source = message, Kind = MessageKind.Unknown, Confidence = 0, MatchReason = "no-keyword"
            });

        public void ReloadRules()
        {
        }
    }

    private sealed class FakeFilePipeline : IFilePipelineService
    {
        public List<(Guid FileId, string Subject)> ReassignCalls { get; } = [];
        public List<FileRecord> Records { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
        {
            var record = new FileRecord
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                FileName = fileName,
                MemberOpenId = memberOpenId ?? "",
                GroupOpenId = groupOpenId ?? "",
                Status = FileStatus.Archived,
                ArchivedRelativePath = $"{HomeworkSubjectResolver.Unclassified}/2026-09-06/{fileName}",
                CompletedAt = DateTimeOffset.Now
            };
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
        {
            ReassignCalls.Add((fileId, subject));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FileRecord>>([.. Records]);
    }

    private static MessageRecord Message(string id, string memberOpenId) => new()
    {
        MessageId = id,
        GroupOpenId = "group-1",
        MemberOpenId = memberOpenId,
        SenderNickname = "数学老师",
        Segments =
        [
            new MessageSegment { Type = SegmentTypes.Text, Text = "消息正文" },
            new MessageSegment { Type = SegmentTypes.File, FileName = "讲义.pdf", Url = "https://example.com/f.pdf" }
        ]
    };

    private static SubjectRecognitionSettings Settings(SubjectRecognitionMode mode) => new() { Mode = mode };

    [Fact]
    public async Task Notice_FromBoundMember_FilesReassignedByBinding()
    {
        var bindings = new MemberSubjectBindingStore(Path.Combine(
            Path.GetTempPath(), "classing-tests", "future-file", Guid.NewGuid().ToString("N")));
        bindings.Set("member-1", "物理", "group-1");
        var files = new FakeFilePipeline();
        var dispatch = new MessageDispatchService(
            classifier: new FakeNoticeClassifier(),
            noticeStore: new MessageDumpServiceTests_FakeNoticeStore(),
            filePipeline: files,
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => Settings(SubjectRecognitionMode.MemberSelection));

        await dispatch.ProcessMessageAsync(Message("m1", "member-1"));

        // 通知附件按显式绑定归档（绑定 > 未分类）
        var record = Assert.Single(files.Records);
        Assert.Equal((record.Id, "物理"), Assert.Single(files.ReassignCalls));
        // 归因随记录持久化
        Assert.Equal("member-1", record.MemberOpenId);
        Assert.Equal("group-1", record.GroupOpenId);
    }

    [Fact]
    public async Task Unclassified_FromBoundMember_FilesReassignedByBinding()
    {
        var bindings = new MemberSubjectBindingStore(Path.Combine(
            Path.GetTempPath(), "classing-tests", "future-file", Guid.NewGuid().ToString("N")));
        bindings.Set("member-1", "化学");
        var files = new FakeFilePipeline();
        var dispatch = new MessageDispatchService(
            classifier: new FakeUnknownClassifier(),
            noticeStore: new MessageDumpServiceTests_FakeNoticeStore(),
            filePipeline: files,
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => Settings(SubjectRecognitionMode.MemberSelection));

        await dispatch.ProcessMessageAsync(Message("m1", "member-1"));

        var record = Assert.Single(files.Records);
        Assert.Equal((record.Id, "化学"), Assert.Single(files.ReassignCalls));
    }

    [Fact]
    public async Task Notice_FromUnboundMember_FilesNotReassigned()
    {
        var bindings = new MemberSubjectBindingStore(Path.Combine(
            Path.GetTempPath(), "classing-tests", "future-file", Guid.NewGuid().ToString("N")));
        var files = new FakeFilePipeline();
        var dispatch = new MessageDispatchService(
            classifier: new FakeNoticeClassifier(),
            noticeStore: new MessageDumpServiceTests_FakeNoticeStore(),
            filePipeline: files,
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => Settings(SubjectRecognitionMode.MemberSelection));

        await dispatch.ProcessMessageAsync(Message("m1", "member-1"));

        Assert.Empty(files.ReassignCalls);
    }

    [Fact]
    public async Task KeywordMode_BoundMember_FilesNotReassignedByBinding()
    {
        // Keyword 模式完全不读取绑定（现状行为不变）
        var bindings = new MemberSubjectBindingStore(Path.Combine(
            Path.GetTempPath(), "classing-tests", "future-file", Guid.NewGuid().ToString("N")));
        bindings.Set("member-1", "物理");
        var files = new FakeFilePipeline();
        var dispatch = new MessageDispatchService(
            classifier: new FakeNoticeClassifier(),
            noticeStore: new MessageDumpServiceTests_FakeNoticeStore(),
            filePipeline: files,
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => Settings(SubjectRecognitionMode.Keyword));

        await dispatch.ProcessMessageAsync(Message("m1", "member-1"));

        Assert.Empty(files.ReassignCalls);
    }
}

/// <summary>最小通知存储替身（本文件专用；与既有测试文件各自独立实现）。</summary>
internal sealed class MessageDumpServiceTests_FakeNoticeStore : INoticeStore
{
    public List<NoticeItem> Items { get; } = [];

#pragma warning disable CS0067
    public event EventHandler<NoticeItem>? Changed;
#pragma warning restore CS0067

    public Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null,
        string? groupOpenId = null, CancellationToken ct = default)
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
            GroupOpenId = groupOpenId ?? ""
        };
        Items.Add(item);
        return Task.FromResult(item);
    }

    public Task MarkReadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

    public Task MarkUnreadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

    public Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

    public Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

    public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);
}
