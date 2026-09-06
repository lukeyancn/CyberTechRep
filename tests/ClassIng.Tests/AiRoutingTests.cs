using ClassIng.Plugin.Services.Classification;
using ClassIng.Plugin.Services.Pipeline;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace ClassIng.Tests;

/// <summary>
/// 需求 6：CyberTechRep AI 四用途路由矩阵测试。
/// 覆盖：未配置 AI=现状不变、Primary 跳过关键词、Backup 关键词不中才触发、AI 失败降级不崩溃、
/// 无关键词兜底接线、AiSettings 默认值与序列化往返。
/// </summary>
public sealed class AiRoutingTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ITestOutputHelper _output;

    public AiRoutingTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", "ai-routing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
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

    // ============ 测试替身 ============

    private sealed class FakeKeywordClassifier : ISubjectClassifier
    {
        public SubjectResult? Result { get; set; }
        public int CallCount { get; private set; }

        public string Name => "FakeKeyword";

        public bool IsAvailable => true;

        public Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSubjectAi : IAiProvider
    {
        public SubjectResult? Result { get; set; }
        public bool Available { get; set; } = true;
        public bool Throw { get; set; }
        public int CallCount { get; private set; }

        public string Name => "FakeAi";

        public bool IsAvailable => Available;

        public Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            if (Throw)
            {
                throw new InvalidOperationException("ai boom");
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeKindAi : IMessageKindAiProvider
    {
        public MessageKindVerdict? Verdict { get; set; }
        public bool Available { get; set; } = true;
        public bool Throw { get; set; }
        public int CallCount { get; private set; }

        public string Name => "FakeKindAi";

        public bool IsAvailable => Available;

        public Task<MessageKindVerdict?> ClassifyAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            if (Throw)
            {
                throw new InvalidOperationException("kind ai boom");
            }

            return Task.FromResult(Verdict);
        }
    }

    private sealed class FakeMessageKeywordClassifier : IMessageClassifier
    {
        public ClassifiedMessage Result { get; set; } = new()
        {
            Source = null!,
            Kind = MessageKind.Unknown,
            Confidence = 0,
            MatchReason = "no_keyword_hit"
        };

        public int CallCount { get; private set; }

        public int ReloadCount { get; private set; }

        public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ClassifiedMessage
            {
                Source = message,
                Kind = Result.Kind,
                Confidence = Result.Confidence,
                MatchReason = Result.MatchReason
            });
        }

        public void ReloadRules() => ReloadCount++;
    }

    private sealed class FakeChainRouter : ISubjectChainModeRouter
    {
        public SubjectResult Result { get; set; } = new()
        {
            Subject = "数学", Confidence = 0.9, Source = SubjectSource.CloudLlm, Reason = "fake"
        };

        public bool Throw { get; set; }

        public AiUsageMode? LastMode { get; private set; }

        public bool LastSuppressManualQueue { get; private set; }

        public int CallCount { get; private set; }

        public Task<SubjectResult> ClassifyWithModeAsync(
            string text, string messageId, AiUsageMode mode, bool suppressManualQueue = false, CancellationToken ct = default)
        {
            CallCount++;
            LastMode = mode;
            LastSuppressManualQueue = suppressManualQueue;
            if (Throw)
            {
                throw new InvalidOperationException("chain boom");
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeFallbackClassifier : INoKeywordFallbackClassifier
    {
        public SubjectResult? Result { get; set; }
        public int CallCount { get; private set; }

        public Task<SubjectResult?> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHomeworkStore : IHomeworkStore
    {
        public List<HomeworkItem> Items { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<HomeworkItem>? Changed;
#pragma warning restore CS0067

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            var removed = Items.RemoveAll(i => i.Id == id) > 0;
            return Task.FromResult(removed);
        }

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
        {
            Items.Add(item);
            return Task.FromResult(item);
        }

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items);

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items.Where(i => i.Subject == subject).ToList());

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>(Items);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeFilePipeline : IFilePipelineService
    {
        public List<(Guid Id, string Subject)> Reassigned { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<FileRecord>? FileUpdated;
#pragma warning restore CS0067

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url, CancellationToken ct = default)
            => throw new InvalidOperationException("not used");

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
        {
            Reassigned.Add((fileId, subject));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileRecord>>([]);
    }

    private static SubjectResult Subject(string name, double confidence, SubjectSource source) => new()
    {
        Subject = name,
        Confidence = confidence,
        Source = source,
        Reason = "fake"
    };

    private static MessageRecord Message(string text) => new()
    {
        MessageId = "msg-" + Guid.NewGuid().ToString("N"),
        GroupOpenId = "group",
        ReceivedAt = DateTimeOffset.Now,
        Segments = [new MessageSegment { Type = SegmentTypes.Text, Text = text }]
    };

    /// <summary>构造链：provider 的 AiSettings 可由测试注入/热改。</summary>
    private (SubjectClassifierChain Chain, SubjectChainOptionsProvider Provider,
        FakeKeywordClassifier Keyword, FakeSubjectAi Ai) BuildChain(
        ClassificationSettings? classification = null,
        AiSettings? ai = null,
        bool wireAiSettings = true)
    {
        var classificationSettings = classification ?? new ClassificationSettings();
        var aiSettings = ai ?? new AiSettings();
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => classificationSettings,
            GetAiSettings = wireAiSettings ? () => aiSettings : null,
            DataDirectory = _dataDir
        };
        var keyword = new FakeKeywordClassifier();
        var aiProvider = new FakeSubjectAi();
        var store = new JsonPendingConfirmStore(provider);
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { aiProvider }, store, provider,
            new XunitLogger(_output));
        return (chain, provider, keyword, aiProvider);
    }

    // ============ 用途①：学科分类路由矩阵 ============

    [Fact]
    public async Task 学科_Off模式_关键词命中现状不变_AI不被调用()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Off });
        keyword.Result = Subject("数学", 1.0, SubjectSource.KeywordRule);

        var result = await chain.ClassifyAsync("完成数学作业", "m1");

        Assert.Equal("数学", result.Subject);
        Assert.Equal(SubjectSource.KeywordRule, result.Source);
        Assert.Equal(0, ai.CallCount);
        Assert.False(result.NeedsManualConfirm);
    }

    [Fact]
    public async Task 学科_Off模式_关键词不中_直接兜底_AI不被调用()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Off });

        var result = await chain.ClassifyAsync("完全无关的文本", "m2");

        Assert.Equal("未分类", result.Subject);
        Assert.True(result.NeedsManualConfirm);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task 学科_Backup模式_关键词命中短路_AI不被调用()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Backup });
        keyword.Result = Subject("语文", 1.0, SubjectSource.KeywordRule);

        var result = await chain.ClassifyAsync("背诵古诗并完成练习", "m3");

        Assert.Equal("语文", result.Subject);
        Assert.Equal(0, ai.CallCount);
        Assert.Equal(1, keyword.CallCount);
    }

    [Fact]
    public async Task 学科_Backup模式_关键词不中才触发AI()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Backup });
        ai.Result = Subject("物理", 0.9, SubjectSource.CloudLlm);

        var result = await chain.ClassifyAsync("请研究滑轮组的机械效率", "m4");

        Assert.Equal(1, keyword.CallCount);
        Assert.Equal(1, ai.CallCount);
        Assert.Equal("物理", result.Subject);
        Assert.Equal(SubjectSource.CloudLlm, result.Source);
    }

    [Fact]
    public async Task 学科_Primary模式_跳过关键词直接AI()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Primary });
        keyword.Result = Subject("数学", 1.0, SubjectSource.KeywordRule); // 关键词本可命中，但 Primary 应跳过
        ai.Result = Subject("英语", 0.85, SubjectSource.CloudLlm);

        var result = await chain.ClassifyAsync("完成数学作业", "m5");

        Assert.Equal(0, keyword.CallCount);
        Assert.Equal(1, ai.CallCount);
        Assert.Equal("英语", result.Subject);
        Assert.Equal(SubjectSource.CloudLlm, result.Source);
    }

    [Fact]
    public async Task 学科_Primary模式_AI返回null_降级兜底不崩溃()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Primary });
        ai.Result = null;

        var result = await chain.ClassifyAsync("任意文本", "m6");

        Assert.Equal(0, keyword.CallCount);
        Assert.Equal(1, ai.CallCount);
        Assert.Equal("未分类", result.Subject);
        Assert.True(result.NeedsManualConfirm);
    }

    [Fact]
    public async Task 学科_Primary模式_AI抛异常_降级兜底不崩溃()
    {
        var (chain, _, keyword, ai) = BuildChain(ai: new AiSettings { SubjectClassifyMode = AiUsageMode.Primary });
        ai.Throw = true;

        var result = await chain.ClassifyAsync("任意文本", "m7");

        Assert.Equal("未分类", result.Subject);
        Assert.True(result.NeedsManualConfirm);
    }

    [Fact]
    public async Task 未接线AiSettings_默认Backup_现状行为完全不变()
    {
        // 模拟旧版本配置/未接线场景：GetAiSettings 为 null → 默认 Backup（= 现有三级链语义）
        var (chain, _, keyword, ai) = BuildChain(wireAiSettings: false);
        ai.Result = Subject("化学", 0.9, SubjectSource.CloudLlm);

        // 关键词命中：与现状一致，短路返回
        keyword.Result = Subject("数学", 1.0, SubjectSource.KeywordRule);
        var hit = await chain.ClassifyAsync("完成数学作业", "m8");
        Assert.Equal("数学", hit.Subject);
        Assert.Equal(0, ai.CallCount);

        // 关键词不中：与现状一致，落到 AI 级
        keyword.Result = null;
        var miss = await chain.ClassifyAsync("氧化还原反应配平", "m9");
        Assert.Equal(1, ai.CallCount);
        Assert.Equal("化学", miss.Subject);
        Assert.False(miss.NeedsManualConfirm);
    }

    // ============ 用途②：通知/作业二分类路由矩阵 ============

    private (AiMessageKindRouter Router, SubjectChainOptionsProvider Provider,
        FakeMessageKeywordClassifier Keyword, FakeKindAi Ai) BuildRouter(AiSettings? ai = null)
    {
        var aiSettings = ai ?? new AiSettings();
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => aiSettings,
            DataDirectory = _dataDir
        };
        var keyword = new FakeMessageKeywordClassifier();
        var kindAi = new FakeKindAi();
        var router = new AiMessageKindRouter(keyword, kindAi, provider, new XunitLogger(_output));
        return (router, provider, keyword, kindAi);
    }

    [Fact]
    public async Task 二分类_Off模式_现状不变_AI不被调用()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Off });

        var result = await router.ClassifyAsync(Message("今晚开家长会"));

        Assert.Equal(MessageKind.Unknown, result.Kind);
        Assert.Equal(1, keyword.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task 二分类_Backup模式_关键词命中_AI不被调用()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Backup });
        keyword.Result = new ClassifiedMessage
        {
            Source = null!, Kind = MessageKind.Homework, Confidence = 1.0, MatchReason = "homework_hit=[作业]"
        };

        var result = await router.ClassifyAsync(Message("记得完成作业"));

        Assert.Equal(MessageKind.Homework, result.Kind);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task 二分类_Backup模式_关键词Unknown才问AI()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Backup });
        ai.Verdict = new MessageKindVerdict(MessageKind.Notice, 0.9, "broadcast");

        var result = await router.ClassifyAsync(Message("今晚七点家长会"));

        Assert.Equal(1, ai.CallCount);
        Assert.Equal(MessageKind.Notice, result.Kind);
        Assert.Contains("ai_message_kind", result.MatchReason);
    }

    [Fact]
    public async Task 二分类_Backup模式_AI失败_降级回关键词结果不崩溃()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Backup });
        ai.Verdict = null; // AI 返回 null（失败）

        var result = await router.ClassifyAsync(Message("一段没有关键词的文本"));

        Assert.Equal(MessageKind.Unknown, result.Kind); // 与现状一致：消息将被忽略
        Assert.Equal(1, ai.CallCount);
    }

    [Fact]
    public async Task 二分类_Backup模式_AI不可用_不调用()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Backup });
        ai.Available = false;

        var result = await router.ClassifyAsync(Message("一段没有关键词的文本"));

        Assert.Equal(MessageKind.Unknown, result.Kind);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task 二分类_Primary模式_跳过关键词_每条消息问AI()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Primary });
        ai.Verdict = new MessageKindVerdict(MessageKind.Homework, 0.88, "assignment");

        var result = await router.ClassifyAsync(Message("记得完成作业"));

        Assert.Equal(0, keyword.CallCount);
        Assert.Equal(1, ai.CallCount);
        Assert.Equal(MessageKind.Homework, result.Kind);
    }

    [Fact]
    public async Task 二分类_Primary模式_AI抛异常_降级关键词不崩溃()
    {
        var (router, _, keyword, ai) = BuildRouter(new AiSettings { MessageClassifyMode = AiUsageMode.Primary });
        ai.Throw = true;
        keyword.Result = new ClassifiedMessage
        {
            Source = null!, Kind = MessageKind.Notice, Confidence = 1.0, MatchReason = "notice_hit=[通知]"
        };

        var result = await router.ClassifyAsync(Message("学校通知"));

        Assert.Equal(1, ai.CallCount);
        Assert.Equal(1, keyword.CallCount);
        Assert.Equal(MessageKind.Notice, result.Kind);
    }

    [Fact]
    public async Task 二分类_Router透传ReloadRules()
    {
        var (router, _, keyword, _) = BuildRouter();
        router.ReloadRules();
        Assert.Equal(1, keyword.ReloadCount);
    }

    // ============ 用途③：无关键词兜底 ============

    [Fact]
    public async Task 兜底_Off模式_不调用链_返回null()
    {
        var chain = new FakeChainRouter();
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { NoKeywordFallbackMode = AiUsageMode.Off },
            DataDirectory = _dataDir
        };
        var fallback = new NoKeywordFallbackClassifier(chain, provider, new XunitLogger(_output));

        var result = await fallback.ClassifyAsync("一段没有关键词的文本", "m10");

        Assert.Null(result);
        Assert.Equal(0, chain.CallCount);
    }

    [Fact]
    public async Task 兜底_Backup模式_完整链_可信结果返回()
    {
        var chain = new FakeChainRouter();
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { NoKeywordFallbackMode = AiUsageMode.Backup },
            DataDirectory = _dataDir
        };
        var fallback = new NoKeywordFallbackClassifier(chain, provider, new XunitLogger(_output));

        var result = await fallback.ClassifyAsync("三角函数化简求值", "m11");

        Assert.NotNull(result);
        Assert.Equal("数学", result.Subject);
        Assert.Equal(AiUsageMode.Backup, chain.LastMode);
        Assert.True(chain.LastSuppressManualQueue); // 抑制人工队列投递
    }

    [Fact]
    public async Task 兜底_Primary模式_链按Primary执行()
    {
        var chain = new FakeChainRouter();
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { NoKeywordFallbackMode = AiUsageMode.Primary },
            DataDirectory = _dataDir
        };
        var fallback = new NoKeywordFallbackClassifier(chain, provider, new XunitLogger(_output));

        var result = await fallback.ClassifyAsync("三角函数化简求值", "m12");

        Assert.NotNull(result);
        Assert.Equal(AiUsageMode.Primary, chain.LastMode);
    }

    [Fact]
    public async Task 兜底_低置信结果_返回null保持忽略现状()
    {
        var chain = new FakeChainRouter
        {
            Result = new SubjectResult
            {
                Subject = "历史", Confidence = 0.3, Source = SubjectSource.CloudLlm, Reason = "low",
                NeedsManualConfirm = true
            }
        };
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { NoKeywordFallbackMode = AiUsageMode.Backup },
            DataDirectory = _dataDir
        };
        var fallback = new NoKeywordFallbackClassifier(chain, provider, new XunitLogger(_output));

        var result = await fallback.ClassifyAsync("一段没有关键词的文本", "m13");

        Assert.Null(result);
    }

    [Fact]
    public async Task 兜底_链异常_返回null不崩溃()
    {
        var chain = new FakeChainRouter { Throw = true };
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { NoKeywordFallbackMode = AiUsageMode.Backup },
            DataDirectory = _dataDir
        };
        var fallback = new NoKeywordFallbackClassifier(chain, provider, new XunitLogger(_output));

        var result = await fallback.ClassifyAsync("一段没有关键词的文本", "m14");

        Assert.Null(result);
    }

    [Fact]
    public async Task 兜底_空文本_不调用链()
    {
        var chain = new FakeChainRouter();
        var provider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { NoKeywordFallbackMode = AiUsageMode.Backup },
            DataDirectory = _dataDir
        };
        var fallback = new NoKeywordFallbackClassifier(chain, provider, new XunitLogger(_output));

        Assert.Null(await fallback.ClassifyAsync("", "m15"));
        Assert.Equal(0, chain.CallCount);
    }

    // ============ 分发层接线：无关键词兜底 → 作业归档 ============

    [Fact]
    public async Task 分发_无关键词兜底命中_作业写入存储()
    {
        var fallback = new FakeFallbackClassifier
        {
            Result = Subject("生物", 0.85, SubjectSource.CloudLlm)
        };
        var store = new FakeHomeworkStore();
        var files = new FakeFilePipeline();
        var dispatch = new MessageDispatchService(
            classifier: new FakeMessageKeywordClassifier(), // 恒 Unknown
            homeworkStore: store,
            filePipeline: files,
            noKeywordFallback: fallback);

        var message = Message("细胞结构与功能");
        await dispatch.ProcessMessageAsync(message);

        var item = Assert.Single(store.Items);
        Assert.Equal("生物", item.Subject);
        Assert.Equal(SubjectSource.CloudLlm, item.SubjectSource);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task 分发_兜底未启用或未命中_保持现状忽略()
    {
        var fallback = new FakeFallbackClassifier { Result = null }; // Off 模式语义
        var store = new FakeHomeworkStore();
        var dispatch = new MessageDispatchService(
            classifier: new FakeMessageKeywordClassifier(),
            homeworkStore: store,
            filePipeline: new FakeFilePipeline(),
            noKeywordFallback: fallback);

        await dispatch.ProcessMessageAsync(Message("一段没有关键词的文本"));

        Assert.Empty(store.Items);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task 分发_兜底分类器未注入_现状不变()
    {
        var store = new FakeHomeworkStore();
        var dispatch = new MessageDispatchService(
            classifier: new FakeMessageKeywordClassifier(),
            homeworkStore: store,
            filePipeline: new FakeFilePipeline());

        await dispatch.ProcessMessageAsync(Message("一段没有关键词的文本"));

        Assert.Empty(store.Items);
    }

    // ============ AiSettings 默认值与序列化 ============

    [Fact]
    public void AiSettings_默认值保证未配置AI时现状不变()
    {
        var ai = new AiSettings();
        Assert.Equal(AiUsageMode.Backup, ai.SubjectClassifyMode); // 学科链现状 = Backup 语义
        Assert.Equal(AiUsageMode.Off, ai.MessageClassifyMode);    // 二分类现状无 AI
        Assert.Equal(AiUsageMode.Off, ai.NoKeywordFallbackMode);  // 兜底现状为忽略
        Assert.Equal(0, ai.CloudTemperature);
    }

    [Fact]
    public void AiSettings_序列化往返保持字段()
    {
        var settings = new AppSettings
        {
            Ai = new AiSettings
            {
                SubjectClassifyMode = AiUsageMode.Primary,
                MessageClassifyMode = AiUsageMode.Backup,
                NoKeywordFallbackMode = AiUsageMode.Primary,
                CloudTemperature = 0.3
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(AiUsageMode.Primary, restored!.Ai.SubjectClassifyMode);
        Assert.Equal(AiUsageMode.Backup, restored.Ai.MessageClassifyMode);
        Assert.Equal(AiUsageMode.Primary, restored.Ai.NoKeywordFallbackMode);
        Assert.Equal(0.3, restored.Ai.CloudTemperature);
    }
}
