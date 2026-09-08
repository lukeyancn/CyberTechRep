using System.Runtime.Versioning;
using Avalonia.Controls;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Services.Pipeline;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 2：成员学科显式绑定存储（member-subject-bindings.json）。
/// 增删查、群/全局作用域优先级、持久化往返、.bak 损坏恢复。
/// </summary>
public sealed class MemberSubjectBindingStoreTests : IDisposable
{
    private readonly string _dir;

    public MemberSubjectBindingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "member-bindings", Guid.NewGuid().ToString("N"));
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
    public void Set_TryGet_Roundtrip()
    {
        var store = new MemberSubjectBindingStore(_dir);
        store.Set("member-1", "数学");

        Assert.True(store.TryGetSubject("member-1", null, out var subject));
        Assert.Equal("数学", subject);
        Assert.Equal("数学", store.GetSubject("member-1", "any-group"));
        Assert.False(store.TryGetSubject("member-2", null, out _));
    }

    [Fact]
    public void GroupBinding_OverridesGlobal_PerGroupOnly()
    {
        var store = new MemberSubjectBindingStore(_dir);
        store.Set("m1", "数学");                 // 全局
        store.Set("m1", "物理", "group-a");      // 群覆盖

        Assert.Equal("物理", store.GetSubject("m1", "group-a"));
        Assert.Equal("数学", store.GetSubject("m1", "group-b"));
        Assert.Equal("数学", store.GetSubject("m1", null));
    }

    [Fact]
    public void Remove_And_RemoveAll()
    {
        var store = new MemberSubjectBindingStore(_dir);
        store.Set("m1", "数学");
        store.Set("m1", "物理", "group-a");

        Assert.True(store.Remove("m1", "group-a"));
        // 群绑定删除后回落到全局绑定（查询语义：群内 → 全局 → 无）
        Assert.Equal("数学", store.GetSubject("m1", "group-a"));
        Assert.Equal("数学", store.GetSubject("m1"));

        Assert.True(store.RemoveAll("m1"));
        Assert.Null(store.GetSubject("m1"));
        Assert.False(store.Remove("m1"));
    }

    [Fact]
    public void Persistence_Roundtrip_AcrossInstances()
    {
        var store = new MemberSubjectBindingStore(_dir);
        store.Set("m1", "英语");
        store.Set("m2", "化学", "group-x");
        store.RemoveAll("m2"); // 已删条目不得复活

        var reloaded = new MemberSubjectBindingStore(_dir);
        Assert.Equal("英语", reloaded.GetSubject("m1"));
        Assert.Null(reloaded.GetSubject("m2", "group-x"));
        var all = reloaded.GetAll();
        Assert.Single(all, all.Single(b => b.MemberOpenId == "m1" && b.Subject == "英语"));
    }

    [Fact]
    public void CorruptMainFile_RestoresFromBak()
    {
        var store = new MemberSubjectBindingStore(_dir);
        store.Set("m1", "数学");
        var bakPath = Path.Combine(_dir, "member-subject-bindings.json.bak");
        Assert.True(File.Exists(bakPath));

        // 主文件损坏 → .bak 恢复
        File.WriteAllText(Path.Combine(_dir, "member-subject-bindings.json"), "{ not valid json");
        var reloaded = new MemberSubjectBindingStore(_dir);
        Assert.Equal("数学", reloaded.GetSubject("m1"));
    }

    [Fact]
    public void GetAll_ContainsGroupScopedEntries()
    {
        var store = new MemberSubjectBindingStore(_dir);
        store.Set("m1", "数学");
        store.Set("m1", "物理", "g1");

        var all = store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, b => b.MemberOpenId == "m1" && b.GroupOpenId.Length == 0 && b.Subject == "数学");
        Assert.Contains(all, b => b.MemberOpenId == "m1" && b.GroupOpenId == "g1" && b.Subject == "物理");
    }
}

/// <summary>
/// 需求 4：学科识别模式路由（MessageDispatchService）。
/// Keyword = 现状逐字节一致（不读绑定、不触发选择窗）；MemberSelection = 绑定优先 → 关键词链 → 未绑定触发选择窗。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SubjectRecognitionRoutingTests : IDisposable
{
    private readonly string _dir;

    public SubjectRecognitionRoutingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "subject-routing", Guid.NewGuid().ToString("N"));
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

    private sealed class FakeClassifier : IMessageClassifier
    {
        /// <summary>分类结果（默认作业；文件消息触发用例改 Unknown 模拟纯文件无文本消息）。</summary>
        public MessageKind Kind { get; init; } = MessageKind.Homework;

        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default) =>
            Task.FromResult(new ClassifiedMessage
            {
                Source = message,
                Kind = Kind,
                Confidence = 1.0,
                MatchReason = "homework-keyword"
            });

        public void ReloadRules()
        {
        }
    }

    /// <summary>最小文件管道替身（文件消息触发用例：断言附件入队与绑定归类）。</summary>
    private sealed class FakeFilePipeline : IFilePipelineService
    {
        public List<(string MessageId, string FileName, string? Url)> EnqueueCalls { get; } = [];

        public List<(Guid FileId, string Subject)> ReassignCalls { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
        {
            EnqueueCalls.Add((messageId, fileName, url));
            var record = new FileRecord
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                FileName = fileName,
                MemberOpenId = memberOpenId ?? "",
                GroupOpenId = groupOpenId ?? "",
                Status = FileStatus.Archived,
                CompletedAt = DateTimeOffset.Now
            };
            return Task.FromResult(record);
        }

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
        {
            ReassignCalls.Add((fileId, subject));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FileRecord>>([]);
    }

    private sealed class FakeSubjectChain : ISubjectClassifierChain
    {
        public SubjectResult Result { get; set; } = new()
        {
            Subject = "数学", Confidence = 0.95, Source = SubjectSource.KeywordRule
        };

        public int CallCount { get; private set; }

        public Task<SubjectResult> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>通知分类替身：选择窗通知触发路径用。</summary>
    private sealed class FakeNoticeClassifier : IMessageClassifier
    {
        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default) =>
            Task.FromResult(new ClassifiedMessage
            {
                Source = message,
                Kind = MessageKind.Notice,
                Confidence = 1.0,
                MatchReason = "notice-keyword"
            });

        public void ReloadRules()
        {
        }
    }

    /// <summary>最小通知存储替身。</summary>
    private sealed class FakeNoticeStore : INoticeStore
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

            var item = new NoticeItem { MessageId = messageId, Content = content, CreatedAt = DateTimeOffset.Now };
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

    private sealed class FakeHomeworkStore : IHomeworkStore
    {
        public List<HomeworkItem> Items { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<HomeworkItem>? Changed;
#pragma warning restore CS0067

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
        {
            var existing = Items.Find(i => i.MessageId == item.MessageId);
            if (existing is not null)
            {
                Items[Items.IndexOf(existing)] = item;
            }
            else
            {
                Items.Add(item);
            }

            return Task.FromResult(item);
        }

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default)
        {
            var index = Items.FindIndex(i => i.Id == id);
            if (index >= 0)
            {
                var existing = Items[index];
                Items[index] = new HomeworkItem
                {
                    Id = existing.Id, MessageId = existing.MessageId, MemberOpenId = existing.MemberOpenId,
                    Subject = subject, SubjectConfidence = 1.0, SubjectSource = SubjectSource.Manual,
                    Content = existing.Content, AttachmentIds = existing.AttachmentIds,
                    CreatedAt = existing.CreatedAt, IsResolved = existing.IsResolved
                };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items]);

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.Where(i => i.Subject == subject).ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items]);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static MessageRecord Message(string memberOpenId = "member-1") => new()
    {
        MessageId = $"msg-{memberOpenId}",
        GroupOpenId = "group-1",
        MemberOpenId = memberOpenId,
        SenderNickname = "数学老师",
        Segments =
        [
            new MessageSegment { Type = SegmentTypes.Text, Text = "今天的数学作业是第 3 页" }
        ]
    };

    /// <summary>纯文件消息（无文本段 → 分类器走 Unknown；2026-09-08 文件消息触发用例）。</summary>
    private static MessageRecord FileMessage(string messageId = "msg-file", string memberOpenId = "member-1") => new()
    {
        MessageId = messageId,
        GroupOpenId = "group-1",
        MemberOpenId = memberOpenId,
        SenderNickname = "数学老师",
        Segments =
        [
            new MessageSegment
            {
                Type = SegmentTypes.File,
                Url = "https://example.com/wb.pdf",
                FileName = "wb.pdf"
            }
        ]
    };

    private static SubjectRecognitionSettings Settings(
        SubjectRecognitionMode mode, bool selectionWindowEnabled = true) => new()
    {
        Mode = mode,
        SelectionWindowEnabled = selectionWindowEnabled
    };

    private (MessageDispatchService Dispatch, FakeSubjectChain Chain, FakeHomeworkStore Homework,
        MemberSubjectBindingStore Bindings, List<SubjectSelectionRequest> Requests) Create(
        SubjectRecognitionSettings settings, Action<MemberSubjectBindingStore>? seedBindings = null,
        FakeClassifier? classifier = null, FakeFilePipeline? files = null)
    {
        var bindings = new MemberSubjectBindingStore(_dir);
        seedBindings?.Invoke(bindings);
        var chain = new FakeSubjectChain();
        var homework = new FakeHomeworkStore();
        var requests = new List<SubjectSelectionRequest>();
        var dispatch = new MessageDispatchService(
            classifier: classifier ?? new FakeClassifier(),
            subjectChain: chain,
            homeworkStore: homework,
            filePipeline: files,
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => settings);
        dispatch.SubjectSelectionRequired += (_, r) => requests.Add(r);
        return (dispatch, chain, homework, bindings, requests);
    }

    // ============ Keyword 模式（红线：现状完全一致） ============

    [Fact]
    public async Task KeywordMode_ChainResultWins_BindingNotRead()
    {
        var (dispatch, chain, homework, bindings, requests) = Create(Settings(SubjectRecognitionMode.Keyword));
        bindings.Set("member-1", "物理"); // 绑定存在但 Keyword 模式不读取

        await dispatch.ProcessMessageAsync(Message());

        Assert.Equal(1, chain.CallCount);
        var item = Assert.Single(homework.Items);
        Assert.Equal("数学", item.Subject);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task KeywordMode_Unclassified_DoesNotRaiseSelection()
    {
        var (dispatch, chain, homework, _, requests) = Create(Settings(SubjectRecognitionMode.Keyword));
        chain.Result = new SubjectResult
        {
            Subject = HomeworkSubjectResolver.Unclassified, Confidence = 0,
            Source = SubjectSource.Manual, NeedsManualConfirm = true
        };

        await dispatch.ProcessMessageAsync(Message());

        Assert.Empty(requests);
        // 现状降级语义：未分类条目仍写入作业存储
        Assert.Single(homework.Items);
    }

    // ============ MemberSelection 模式（默认） ============

    [Fact]
    public async Task MemberSelection_BindingWins_ChainNotCalled()
    {
        var (dispatch, chain, homework, bindings, requests) = Create(
            Settings(SubjectRecognitionMode.MemberSelection),
            seedBindings: b => b.Set("member-1", "物理"));

        await dispatch.ProcessMessageAsync(Message());

        Assert.Equal(0, chain.CallCount);
        var item = Assert.Single(homework.Items);
        Assert.Equal("物理", item.Subject);
        Assert.Equal(SubjectSource.Manual, item.SubjectSource);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task MemberSelection_NoBinding_ChainRuns_AndStillRaisesSelection()
    {
        var (dispatch, chain, homework, _, requests) = Create(Settings(SubjectRecognitionMode.MemberSelection));

        await dispatch.ProcessMessageAsync(Message());

        Assert.Equal(1, chain.CallCount);
        var item = Assert.Single(homework.Items);
        Assert.Equal("数学", item.Subject);
        // 2026-09-06 语义修正：即使关键词链已识别出学科，未绑定也触发选择窗
        //（候选把已识别结果放首位），点选写回绑定后该成员消息走绑定直达。
        var request = Assert.Single(requests);
        Assert.Equal("数学", request.ChainSubject);
        Assert.Equal(0.95, request.ChainConfidence);
    }

    [Fact]
    public async Task MemberSelection_Notice_Unbound_RaisesSelection()
    {
        var bindings = new MemberSubjectBindingStore(_dir);
        var noticeStore = new FakeNoticeStore();
        var requests = new List<SubjectSelectionRequest>();
        var dispatch = new MessageDispatchService(
            classifier: new FakeNoticeClassifier(),
            noticeStore: noticeStore,
            homeworkStore: new FakeHomeworkStore(),
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => Settings(SubjectRecognitionMode.MemberSelection));
        dispatch.SubjectSelectionRequired += (_, r) => requests.Add(r);

        await dispatch.ProcessMessageAsync(Message());

        // 真机缺陷场景（2026-09-06 日志 22:16:09）：通知从不触发选择窗 → 已修复
        var request = Assert.Single(requests);
        Assert.Equal("msg-member-1", request.MessageId);
        Assert.Equal("member-1", request.MemberOpenId);
        Assert.Single(noticeStore.Items); // 主流程不阻塞：通知照常入库
    }

    [Fact]
    public async Task MemberSelection_Notice_Bound_DoesNotRaiseSelection()
    {
        var bindings = new MemberSubjectBindingStore(_dir);
        bindings.Set("member-1", "物理");
        var noticeStore = new FakeNoticeStore();
        var requests = new List<SubjectSelectionRequest>();
        var dispatch = new MessageDispatchService(
            classifier: new FakeNoticeClassifier(),
            noticeStore: noticeStore,
            homeworkStore: new FakeHomeworkStore(),
            memberBindings: bindings,
            getSubjectRecognitionSettings: () => Settings(SubjectRecognitionMode.MemberSelection));
        dispatch.SubjectSelectionRequired += (_, r) => requests.Add(r);

        await dispatch.ProcessMessageAsync(Message());

        Assert.Empty(requests);
    }

    [Fact]
    public async Task MemberSelection_Cooldown_SecondMessageWithinWindow_DoesNotRaiseAgain()
    {
        var (dispatch, _, _, _, requests) = Create(Settings(SubjectRecognitionMode.MemberSelection));

        await dispatch.ProcessMessageAsync(Message());
        // 同一成员的第二条消息（不同 MessageId）在冷却窗口内 → 不重复弹
        var second = Message();
        second = new MessageRecord
        {
            MessageId = "msg-second",
            GroupOpenId = second.GroupOpenId,
            MemberOpenId = second.MemberOpenId,
            SenderNickname = second.SenderNickname,
            Segments = second.Segments
        };
        await dispatch.ProcessMessageAsync(second);

        Assert.Single(requests);
        Assert.Equal("msg-member-1", requests[0].MessageId);
    }

    [Fact]
    public async Task MemberSelection_UnboundAndUnclassified_RaisesSelection()
    {
        var (dispatch, chain, homework, bindings, requests) = Create(Settings(SubjectRecognitionMode.MemberSelection));
        // 链降级到未分类（NeedsManualConfirm）
        chain.Result = new SubjectResult
        {
            Subject = HomeworkSubjectResolver.Unclassified, Confidence = 0,
            Source = SubjectSource.Manual, NeedsManualConfirm = true
        };

        await dispatch.ProcessMessageAsync(Message());

        var request = Assert.Single(requests);
        Assert.Equal("msg-member-1", request.MessageId);
        Assert.Equal("member-1", request.MemberOpenId);
        Assert.Equal("数学老师", request.SenderNickname);
        Assert.Contains("数学作业", request.MessageDigest);
        // 红线：主流程不阻塞——消息仍按现有降级语义写入（未分类条目在库）
        Assert.Single(homework.Items);
        Assert.False(bindings.TryGetSubject("member-1", null, out _)); // 触发≠绑定
    }

    [Fact]
    public async Task MemberSelection_Bound_DoesNotRaiseSelection()
    {
        var (dispatch, chain, _, bindings, requests) = Create(
            Settings(SubjectRecognitionMode.MemberSelection),
            seedBindings: b => b.Set("member-1", "物理"));
        chain.Result = new SubjectResult
        {
            Subject = HomeworkSubjectResolver.Unclassified, Confidence = 0,
            Source = SubjectSource.Manual, NeedsManualConfirm = true
        };

        await dispatch.ProcessMessageAsync(Message());

        Assert.Empty(requests); // 已绑定：不触发
    }

    [Fact]
    public async Task MemberSelection_WindowDisabled_DoesNotRaiseSelection()
    {
        var (dispatch, chain, _, _, requests) = Create(
            Settings(SubjectRecognitionMode.MemberSelection, selectionWindowEnabled: false));
        chain.Result = new SubjectResult
        {
            Subject = HomeworkSubjectResolver.Unclassified, Confidence = 0,
            Source = SubjectSource.Manual, NeedsManualConfirm = true
        };

        await dispatch.ProcessMessageAsync(Message());

        Assert.Empty(requests);
    }

    // ============ 文件消息触发（2026-09-08 修复 Issue A：纯文件消息从不弹选择窗） ============

    [Fact]
    public async Task MemberSelection_FileOnlyMessage_Unbound_RaisesSelection()
    {
        var files = new FakeFilePipeline();
        var (dispatch, _, _, _, requests) = Create(
            Settings(SubjectRecognitionMode.MemberSelection),
            classifier: new FakeClassifier { Kind = MessageKind.Unknown }, // 无文本 → 分类器返回 Unknown
            files: files);

        await dispatch.ProcessMessageAsync(FileMessage());

        // 文件消息同样触发选择悬浮窗（附件要按学科归档 → 需要学科分类）
        var request = Assert.Single(requests);
        Assert.Equal("msg-file", request.MessageId);
        Assert.Equal("member-1", request.MemberOpenId);
        // 主流程不阻塞：文件照常入队文件管道
        var (msgId, fileName, _) = Assert.Single(files.EnqueueCalls);
        Assert.Equal("msg-file", msgId);
        Assert.Equal("wb.pdf", fileName);
    }

    [Fact]
    public async Task MemberSelection_FileOnlyMessage_Bound_DoesNotRaiseSelection()
    {
        var files = new FakeFilePipeline();
        var (dispatch, _, _, bindings, requests) = Create(
            Settings(SubjectRecognitionMode.MemberSelection),
            seedBindings: b => b.Set("member-1", "物理"),
            classifier: new FakeClassifier { Kind = MessageKind.Unknown },
            files: files);

        await dispatch.ProcessMessageAsync(FileMessage());

        Assert.Empty(requests); // 已绑定发送者：不触发（回归红线）
    }

    [Fact]
    public async Task MemberSelection_UnclassifiedTextWithoutAttachments_DoesNotRaiseSelection()
    {
        // 纯文本无关键词消息维持现状：忽略、不弹窗（判定要求 Unknown 类消息必须携带附件段）
        var (dispatch, _, _, _, requests) = Create(
            Settings(SubjectRecognitionMode.MemberSelection),
            classifier: new FakeClassifier { Kind = MessageKind.Unknown });

        await dispatch.ProcessMessageAsync(Message());

        Assert.Empty(requests);
    }

    // ============ 触发状态绝不一次性消费（2026-09-08 修复 Issue B：窗口关闭后可再次触发） ============

    [Fact]
    public async Task MemberSelection_RetriggerAfterCooldown_WindowCloseNeverSilencesPermanently()
    {
        var now = DateTimeOffset.Now;
        var (dispatch, _, _, _, requests) = Create(Settings(SubjectRecognitionMode.MemberSelection));
        dispatch.Clock = () => now; // 假时钟：控制冷却窗口推进

        // 第 1 条消息 → 触发（模拟悬浮窗弹出后被用户关闭/超时隐藏）
        await dispatch.ProcessMessageAsync(Message());
        var first = Assert.Single(requests);
        Assert.Equal("msg-member-1", first.MessageId);

        // 冷却窗口内（45 秒）的第 2 条消息 → 限流跳过（防骚扰）
        now += TimeSpan.FromSeconds(10);
        await dispatch.ProcessMessageAsync(MessageWithId("msg-second"));
        Assert.Single(requests); // 未新增

        // 冷却过期 → 同一未绑定发送者的第 3 条消息再次触发（关闭窗口绝不变成永久静默）
        now += MessageDispatchService.SelectionCooldownWindow;
        await dispatch.ProcessMessageAsync(MessageWithId("msg-third"));

        Assert.Equal(2, requests.Count);
        Assert.Equal("msg-third", requests[1].MessageId);
    }

    /// <summary>同发送者换 MessageId 的消息（冷却限流按「群+成员」而非消息 Id）。</summary>
    private static MessageRecord MessageWithId(string messageId, string memberOpenId = "member-1") => new()
    {
        MessageId = messageId,
        GroupOpenId = "group-1",
        MemberOpenId = memberOpenId,
        SenderNickname = "数学老师",
        Segments =
        [
            new MessageSegment { Type = SegmentTypes.Text, Text = "今天的数学作业是第 3 页" }
        ]
    };

    [Fact]
    public async Task MemberSelection_CooldownIsolated_PerSender()
    {
        var (dispatch, _, _, _, requests) = Create(Settings(SubjectRecognitionMode.MemberSelection));

        // 发送者 A 的文件消息 → 触发
        await dispatch.ProcessMessageAsync(FileMessage(memberOpenId: "member-a"));
        // 同一瞬间发送者 B 的文件消息 → 不受 A 的冷却影响，独立触发
        await dispatch.ProcessMessageAsync(FileMessage(messageId: "msg-file-b", memberOpenId: "member-b"));

        Assert.Equal(2, requests.Count);
        Assert.Equal("member-a", requests[0].MemberOpenId);
        Assert.Equal("member-b", requests[1].MemberOpenId);
    }

    // ============ 触发判定纯逻辑（可见性门控矩阵） ============

    [Fact]
    public void ShouldRaiseSubjectSelection_TruthTable()
    {
        // MemberSelection + 开 + 作业 + 有发送者 + 未绑定 → 触发（与识别链结果无关）
        Assert.True(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, true, MessageKind.Homework,
            hasAttachmentSegments: false, "m1", memberBound: false));
        // MemberSelection + 开 + 通知 + 未绑定 → 触发
        Assert.True(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, true, MessageKind.Notice,
            hasAttachmentSegments: false, "m1", memberBound: false));
        // MemberSelection + 开 + 未分类（无文本的纯文件消息）+ 携带附件段 + 未绑定 → 触发（2026-09-08 文件消息修复）
        Assert.True(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, true, MessageKind.Unknown,
            hasAttachmentSegments: true, "m1", memberBound: false));
        // Keyword 模式 → 永不触发
        Assert.False(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.Keyword, true, MessageKind.Homework,
            hasAttachmentSegments: false, "m1", memberBound: false));
        // 开关关闭 → 不触发
        Assert.False(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, false, MessageKind.Homework,
            hasAttachmentSegments: false, "m1", memberBound: false));
        // 已绑定 → 不触发
        Assert.False(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, true, MessageKind.Homework,
            hasAttachmentSegments: false, "m1", memberBound: true));
        // 无发送者（OpenID 为空）→ 不触发
        Assert.False(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, true, MessageKind.Homework,
            hasAttachmentSegments: false, "", memberBound: false));
        // 未分类消息（Unknown）且无附件段（纯文本无关键词）→ 不触发（现状语义：忽略）
        Assert.False(MessageDispatchService.ShouldRaiseSubjectSelection(
            SubjectRecognitionMode.MemberSelection, true, MessageKind.Unknown,
            hasAttachmentSegments: false, "m1", memberBound: false));
    }

    // ============ 触发语义对齐需求（2026-09-06 修正）：关键词已识别 + 未绑定也触发 ============

    [Fact]
    public void CooldownSlot_WithinWindow_Blocks_SameMemberOnly()
    {
        var dispatch = new MessageDispatchService();
        var now = DateTimeOffset.Now;

        Assert.True(dispatch.TryTakeSelectionTriggerSlot("m1", "g1", now));
        // 同一「群+成员」冷却窗口（45 秒）内 → 拒绝
        Assert.False(dispatch.TryTakeSelectionTriggerSlot(
            "m1", "g1", now.Add(MessageDispatchService.SelectionCooldownWindow - TimeSpan.FromSeconds(1))));
        // 其他成员 / 其他群 → 不受影响
        Assert.True(dispatch.TryTakeSelectionTriggerSlot("m2", "g1", now));
        Assert.True(dispatch.TryTakeSelectionTriggerSlot("m1", "g2", now));
        // 冷却窗口过后 → 放行（窗口关闭后再次触发语义的关键：绝不永久静默）
        Assert.True(dispatch.TryTakeSelectionTriggerSlot(
            "m1", "g1", now + MessageDispatchService.SelectionCooldownWindow + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void MaskMember_NeverContainsOpenIdPlaintext()
    {
        var masked = MessageDispatchService.MaskMember("张老师", "OPENID-SECRET-12345");

        Assert.StartsWith("张老师#", masked);
        Assert.DoesNotContain("OPENID-SECRET", masked);
        // 同一 OpenID 哈希稳定（日志可对比）；昵称缺失有占位
        Assert.Equal(masked, MessageDispatchService.MaskMember("张老师", "OPENID-SECRET-12345"));
        Assert.StartsWith("成员#", MessageDispatchService.MaskMember("", "OPENID-2"));
    }
}

/// <summary>
/// 需求 3：选择悬浮窗展示逻辑与协调器写回。
/// </summary>
public sealed class SubjectSelectionLogicTests
{
    [Fact]
    public void BuildSubjectChoices_ChainCandidateFirst_NoDuplicate_NoUnclassified()
    {
        var choices = SubjectSelectionLogic.BuildSubjectChoices(
            ["数学", "语文", "英语"], chainSubject: "数学");

        Assert.Equal(["数学", "语文", "英语"], choices);
    }

    [Fact]
    public void BuildSubjectChoices_UnclassifiedChain_ExcludedFromChoices()
    {
        var choices = SubjectSelectionLogic.BuildSubjectChoices(
            ["数学", "语文"], HomeworkSubjectResolver.Unclassified);

        Assert.Equal(["数学", "语文"], choices);
    }

    [Fact]
    public void BuildSubjectChoices_EmptyChainSubject_NoticePath_Excluded()
    {
        // 通知不经识别链：空链候选不得混入点选列表
        var choices = SubjectSelectionLogic.BuildSubjectChoices(["数学", "语文"], chainSubject: "");

        Assert.Equal(["数学", "语文"], choices);
    }

    [Fact]
    public void FormatDigest_TruncatesLongText()
    {
        Assert.Equal("（消息无文本内容）", SubjectSelectionLogic.FormatDigest(""));
        var longText = new string('x', SubjectSelectionLogic.DigestMaxLength + 10);
        var digest = SubjectSelectionLogic.FormatDigest(longText);
        Assert.True(digest.Length <= SubjectSelectionLogic.DigestMaxLength + 1);
        Assert.EndsWith("…", digest);
    }

    [Fact]
    public void FormatSender_ShowsNicknameOrFallback()
    {
        Assert.Contains("老师", SubjectSelectionLogic.FormatSender("m1", "老师", "g1"));
        Assert.Contains("未提供昵称", SubjectSelectionLogic.FormatSender("m1", "", ""));
    }
}

/// <summary>需求 3：选择悬浮窗 UI 装载与默认可见性（第五悬浮窗不随宿主显示）。</summary>
public sealed class SubjectSelectionWindowTests
{    [Fact]
    public void SelectionWindow_DefaultsHidden_DoesNotLaunchWithHost()
    {
        var settings = new CyberTechRep.Shared.Models.OverlaySettings();
        Assert.False(settings.Selection.Visible);
        Assert.Equal(320, settings.Selection.Width);
        Assert.Equal(260, settings.Selection.Height);
    }

    [Fact]
    public Task ShowRequest_PopulatesView_AndChoiceCallbackHidesWindow()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            SubjectSelectionRequest? received = null;
            var selected = new TaskCompletionSource<string>();
            var window = new SubjectSelectionSuspensionWindow(
                (request, subject) =>
                {
                    received = request;
                    selected.SetResult(subject);
                    return Task.CompletedTask;
                },
                () => ["数学", "语文"]);

            var request = new SubjectSelectionRequest(
                "msg-1", "member-1", "数学老师", "group-1",
                "今天的数学作业是第 3 页", HomeworkSubjectResolver.Unclassified, 0,
                SubjectSource.Manual, DateTimeOffset.Now);
            window.ShowRequest(request);

            var view = window.CaptureView();
            Assert.Contains("数学老师", view.SenderText);
            Assert.Contains("member-1", view.SenderText);
            Assert.Contains("数学作业", view.DigestText);
            Assert.Equal(["数学", "语文"], view.Subjects);

            // 点选学科 → 回调收到（请求, 学科），窗口隐藏（可见性由控制器同步回设置）
            await window.SelectSubjectAsync("数学");
            Assert.Equal("数学", await selected.Task);
            Assert.Equal("msg-1", received!.MessageId);
            Assert.False(window.IsVisible);
        }, CancellationToken.None);
    }
}

/// <summary>需求 3：协调器用户点选写回（绑定存储 + 作业人工修正 + 文件不抛错）。</summary>
[SupportedOSPlatform("windows")]
public sealed class SubjectSelectionCoordinatorTests : IDisposable
{
    private readonly string _dir;

    public SubjectSelectionCoordinatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "selection-coordinator", Guid.NewGuid().ToString("N"));
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
    public async Task ApplySelection_WritesBinding_AndManualHomeworkSubject()
    {
        var bindings = new MemberSubjectBindingStore(_dir);
        var homework = new HomeworkItem { MessageId = "msg-1", MemberOpenId = "member-1", Content = "作业" };
        var store = new StoreWithUpsert(homework);
        var request = new SubjectSelectionRequest(
            "msg-1", "member-1", "数学老师", "group-1", "作业",
            HomeworkSubjectResolver.Unclassified, 0, SubjectSource.Manual, DateTimeOffset.Now);
        var coordinator = new SubjectSelectionCoordinator(
            memberBindings: bindings, homeworkStore: store);

        await coordinator.ApplySelectionAsync(request, "物理");

        Assert.Equal("物理", bindings.GetSubject("member-1"));
        Assert.Equal("物理", store.Items.Single().Subject);
        Assert.Equal(SubjectSource.Manual, store.Items.Single().SubjectSource);
    }

    [Fact]
    public async Task ApplySelection_EmptySubject_Noop()
    {
        var bindings = new MemberSubjectBindingStore(_dir);
        var coordinator = new SubjectSelectionCoordinator(memberBindings: bindings);
        var request = new SubjectSelectionRequest(
            "msg-1", "member-1", "老师", "g", "作业", "未分类", 0, SubjectSource.Manual, DateTimeOffset.Now);

        await coordinator.ApplySelectionAsync(request, "  ");

        Assert.False(bindings.TryGetSubject("member-1", null, out _));
    }

    // ============ ShowAsync UI 线程接线（2026-09-06 真机缺陷：后台线程构造窗口异常被静默吞掉）============

    private static SubjectSelectionRequest MakeRequest() => new(
        "msg-1", "member-1", "数学老师", "group-1", "作业内容",
        "数学", 0.95, SubjectSource.KeywordRule, DateTimeOffset.Now);

    [Fact]
    public async Task ShowAsync_WindowAccessorThrows_IsSwallowedAndLogged()
    {
        // 此前 windowAccessor() 位于 try 之外：后台线程抛异常 → fire-and-forget 静默失败，窗口永不显示
        var coordinator = new SubjectSelectionCoordinator(
            windowAccessor: () => throw new InvalidOperationException("Call from invalid thread"),
            uiMarshal: action => { action(); return Task.CompletedTask; });

        await coordinator.ShowAsync(MakeRequest()); // 不抛：失败只留日志
    }

    [Fact]
    public async Task ShowAsync_LoadsRequestViaUiMarshal_AndShowsThroughController()
    {
        var shownKeys = new List<string>();
        var controller = new ControllerStub(k => shownKeys.Add(k));
        await AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var window = new SubjectSelectionSuspensionWindow(null, () => ["数学", "语文"]);
            var marshalCalls = 0;
            var coordinator = new SubjectSelectionCoordinator(
                controller: controller,
                windowAccessor: () => window,
                uiMarshal: action =>
                {
                    marshalCalls++;
                    action();
                    return Task.CompletedTask;
                });

            await coordinator.ShowAsync(MakeRequest());

            // 请求内容已装载（经 UI 线程），控制器按 subjectSelection key 显示
            Assert.Equal(1, marshalCalls);
            Assert.Contains("作业内容", window.CaptureView().DigestText);
        }, CancellationToken.None);
        Assert.Equal(["subjectSelection"], shownKeys);
    }

    /// <summary>控制器替身：记录 ShowAsync 调用的 overlayKey。</summary>
    private sealed class ControllerStub(Action<string> onShow) : ISuspensionWindowController
    {
        public Task ShowAsync(string overlayKey, CancellationToken ct = default)
        {
            onShow(overlayKey);
            return Task.CompletedTask;
        }

        public Task HideAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetPositionAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task ApplySettingsAsync(string overlayKey, CyberTechRep.Shared.Models.OverlayWindowSettings settings, CancellationToken ct = default) =>
            Task.CompletedTask;

        public bool IsOnScreen(string overlayKey) => true;

        public void NotifyHostStopping()
        {
        }
    }

    /// <summary>带 SetSubjectAsync 的最小作业存储替身（复用 SubjectRecognitionRoutingTests 的 FakeHomeworkStore 不可见，此处独立实现）。</summary>
    private sealed class StoreWithUpsert(HomeworkItem seed) : IHomeworkStore
    {
        public List<HomeworkItem> Items { get; } = [seed];

#pragma warning disable CS0067
        public event EventHandler<HomeworkItem>? Changed;
#pragma warning restore CS0067

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default) =>
            Task.FromResult(item);

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default)
        {
            var index = Items.FindIndex(i => i.Id == id);
            if (index >= 0)
            {
                var existing = Items[index];
                Items[index] = new HomeworkItem
                {
                    Id = existing.Id, MessageId = existing.MessageId, MemberOpenId = existing.MemberOpenId,
                    Subject = subject, SubjectConfidence = 1.0, SubjectSource = SubjectSource.Manual,
                    Content = existing.Content, AttachmentIds = existing.AttachmentIds,
                    CreatedAt = existing.CreatedAt, IsResolved = existing.IsResolved
                };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items]);

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.Where(i => i.Subject == subject).ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HomeworkItem>>([.. Items]);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
