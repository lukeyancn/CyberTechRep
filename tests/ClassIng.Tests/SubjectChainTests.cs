using System.Net;
using System.Text;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace ClassIng.Tests;

/// <summary>模块 3 单元测试：三级学科识别链（关键词 → AI → 人工兜底）。</summary>
public class SubjectChainTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ITestOutputHelper _output;

    public SubjectChainTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
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

    // ---------- 测试替身 ----------

    private class FakeAiProvider(
        string name,
        SubjectSource source,
        SubjectResult? result,
        bool isLocal = false,
        bool isAvailable = true) : IAiProvider
    {
        public string Name { get; } = name;
        public bool IsLocal { get; } = isLocal;
        public int CallCount { get; private set; }

        public bool IsAvailable { get; } = isAvailable;

        public Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeAiThrowingProvider : IAiProvider
    {
        public string Name => "ThrowingAi";
        public bool IsAvailable => true;

        public Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    /// <summary>不实现 ILocalAiProvider 的假本地（用于排序验证）——由 isLocal 标记驱动。</summary>
    private sealed class LocalFakeAiProvider(SubjectResult? result, bool isAvailable = true)
        : FakeAiProvider("LocalOnnx", SubjectSource.LocalModel, result, isLocal: true, isAvailable: isAvailable),
            ILocalAiProvider;

    private SubjectChainOptionsProvider CreateProvider(
        double confidenceThreshold = 0.7,
        bool preferLocal = true,
        bool aiEnabled = true)
    {
        ClassificationSettings settings = new()
        {
            ManualConfirmQueueEnabled = true
        };
        AiSettings aiSettings = new()
        {
            ConfidenceThreshold = confidenceThreshold,
            PreferLocalModel = preferLocal,
            AiEnabled = aiEnabled
        };
        return new SubjectChainOptionsProvider
        {
            GetSettings = () => settings,
            GetAiSettings = () => aiSettings,
            DataDirectory = _dataDir
        };
    }

    private KeywordSubjectClassifier CreateKeyword(SubjectChainOptionsProvider provider) =>
        new(provider, new XunitLogger(_output));

    private static SubjectResult Result(string subject, double confidence, SubjectSource source) => new()
    {
        Subject = subject,
        Confidence = confidence,
        Source = source,
        Reason = "fake"
    };

    // ---------- ①关键词级 ----------

    [Fact]
    public async Task 关键词命中_返回KeywordRule且置信度达标()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var chain = new SubjectClassifierChain(keyword, Array.Empty<IAiProvider>(),
            new JsonPendingConfirmStore(provider), provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("今晚请完成数学练习并提交", "msg-kw-1");

        Assert.Equal("数学", result.Subject);
        Assert.Equal(SubjectSource.KeywordRule, result.Source);
        Assert.Equal(1.0, result.Confidence);
        Assert.False(result.NeedsManualConfirm);
    }

    [Fact]
    public async Task 关键词未命中_自动进入AI级()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var fakeAi = new LocalFakeAiProvider(Result("物理", 0.95, SubjectSource.LocalModel));
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { fakeAi },
            new JsonPendingConfirmStore(provider), provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("请研究滑轮组的机械效率", "msg-ai-1");

        Assert.Equal(1, fakeAi.CallCount);
        Assert.Equal("物理", result.Subject);
        Assert.Equal(SubjectSource.LocalModel, result.Source);
        Assert.False(result.NeedsManualConfirm);
    }

    [Fact]
    public async Task 本地模型缺失_跳过本地_使用云端()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        // 真实 LocalOnnxAiProvider：模型文件不存在 → IsAvailable=false 自动降级
        var local = new LocalOnnxAiProvider(provider, new XunitLogger(_output));
        Assert.False(local.IsAvailable);

        var cloud = new FakeAiProvider("CloudLlm", SubjectSource.CloudLlm,
            Result("化学", 0.9, SubjectSource.CloudLlm));
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { local, cloud },
            new JsonPendingConfirmStore(provider), provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("请推导天体轨道运动的参数", "msg-local-missing");

        Assert.False(local.IsAvailable);
        Assert.Equal(1, cloud.CallCount);
        Assert.Equal("化学", result.Subject);
        Assert.Equal(SubjectSource.CloudLlm, result.Source);
    }

    // ---------- ②AI 低置信 / 失败 → ③人工 ----------

    [Fact]
    public async Task AI低置信_进人工队列且NeedsManualConfirm()
    {
        var provider = CreateProvider(confidenceThreshold: 0.7);
        var keyword = CreateKeyword(provider);
        var fakeAi = new LocalFakeAiProvider(Result("历史", 0.4, SubjectSource.LocalModel));
        var store = new JsonPendingConfirmStore(provider);
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { fakeAi },
            store, provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("阅读材料并回答问题", "msg-low-1");

        Assert.Equal("历史", result.Subject);
        Assert.True(result.NeedsManualConfirm);

        var waiting = await store.GetWaitingAsync();
        var item = Assert.Single(waiting);
        Assert.Equal("msg-low-1", item.MessageId);
        Assert.Equal(PendingConfirmStatus.Waiting, item.Status);
        Assert.Single(item.Candidates);
        Assert.Equal(0.4, item.Candidates[0].Confidence);
    }

    [Fact]
    public async Task AI全部不可用_兜底未分类_永不静默丢失()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var unavailableAi = new LocalFakeAiProvider(null, isAvailable: false);
        var throwingAi = new FakeAiThrowingProvider();
        var store = new JsonPendingConfirmStore(provider);
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { unavailableAi, throwingAi },
            store, provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("一段完全无法识别的文本", "msg-fallback-1");

        // 永不返回 null / 永不静默丢失：总有结果，且 NeedsManualConfirm=true
        Assert.NotNull(result);
        Assert.Equal("未分类", result.Subject);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(SubjectSource.Manual, result.Source);
        Assert.True(result.NeedsManualConfirm);

        var waiting = await store.GetWaitingAsync();
        Assert.Single(waiting);
    }

    [Fact]
    public async Task 空文本_链仍返回非null并进人工队列()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var store = new JsonPendingConfirmStore(provider);
        var chain = new SubjectClassifierChain(keyword, Array.Empty<IAiProvider>(),
            store, provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("", "msg-empty");

        Assert.NotNull(result);
        Assert.True(result.NeedsManualConfirm);
        Assert.Single(await store.GetWaitingAsync());
    }

    // ---------- ③人工确认队列（持久化 + Resolve 写回） ----------

    [Fact]
    public async Task 人工Resolve_状态与学科写回并持久化()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var fakeAi = new LocalFakeAiProvider(Result("地理", 0.35, SubjectSource.LocalModel));
        var store = new JsonPendingConfirmStore(provider);
        PendingConfirmItem? handlerReceived = null;
        store.Resolved = (item, _) =>
        {
            handlerReceived = item;
            return Task.CompletedTask;
        };
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { fakeAi },
            store, provider, new XunitLogger(_output));

        var lowResult = await chain.ClassifyAsync("结合图文资料完成下列问题", "msg-resolve-1");
        Assert.True(lowResult.NeedsManualConfirm);

        var waiting = await store.GetWaitingAsync();
        var item = Assert.Single(waiting);

        await store.ResolveAsync(item.Id, "地理");

        // Resolve 后：不再出现在等待队列
        Assert.Empty(await store.GetWaitingAsync());
        // 写回钩子收到人工选择结果
        Assert.NotNull(handlerReceived);
        Assert.Equal("地理", handlerReceived!.ResolvedSubject);
        Assert.Equal(PendingConfirmStatus.Resolved, handlerReceived.Status);

        // 持久化验证：新实例重载文件，条目已 Resolved
        var store2 = new JsonPendingConfirmStore(provider);
        var all = await store2.GetWaitingAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task 同一消息重复投递_幂等不重复入队()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var store = new JsonPendingConfirmStore(provider);
        var chain = new SubjectClassifierChain(keyword, Array.Empty<IAiProvider>(),
            store, provider, new XunitLogger(_output));

        await chain.ClassifyAsync("无法识别内容A", "msg-dup");
        await chain.ClassifyAsync("无法识别内容B", "msg-dup");

        Assert.Single(await store.GetWaitingAsync());
    }

    // ---------- 排序 / 设置 ----------

    [Fact]
    public void PreferLocalModelfalse_云端排在本地之前()
    {
        var provider = CreateProvider(preferLocal: false);
        var keyword = CreateKeyword(provider);
        var local = new LocalFakeAiProvider(null);
        var cloud = new FakeAiProvider("CloudLlm", SubjectSource.CloudLlm, null);
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { local, cloud },
            new JsonPendingConfirmStore(provider), provider);

        var ordered = chain.OrderAiProviders();
        Assert.Same(cloud, ordered[0]);
        Assert.Same(local, ordered[1]);
    }

    // ---------- 云端 OpenAI 兼容 API（HttpClient 可测实现） ----------

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            return responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private SubjectChainOptionsProvider CreateCloudProvider(
        int dailyLimit = 200,
        Func<string, string>? unprotector = null,
        CloudAiProvider cloudProvider = CloudAiProvider.OpenAiCompatible)
    {
        ClassificationSettings settings = new();
        AiSettings aiSettings = new()
        {
            CloudProvider = cloudProvider,
            CloudEndpoint = "https://llm.example.com/v1",
            CloudApiKeyProtected = "protected-key",
            CloudModelName = "test-model",
            CloudDailyCallLimit = dailyLimit
        };
        return new SubjectChainOptionsProvider
        {
            GetSettings = () => settings,
            GetAiSettings = () => aiSettings,
            DataDirectory = _dataDir,
            SecretUnprotector = unprotector ?? (_ => "plain-key")
        };
    }

    [Fact]
    public async Task 云端识别_成功解析OpenAI兼容响应()
    {
        var provider = CreateCloudProvider();
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """
            {
              "choices": [
                { "message": { "content": "{\"subject\":\"英语\",\"confidence\":0.88,\"reason\":\"出现单词听写\"}" } }
              ]
            }
            """));
        var cloud = new CloudOpenAiProvider(provider, new HttpClient(handler), new XunitLogger(_output));

        Assert.True(cloud.IsAvailable);
        var result = await cloud.ClassifyAsync("默写unit3单词并完成听力练习");

        Assert.NotNull(result);
        Assert.Equal("英语", result.Subject);
        Assert.Equal(0.88, result.Confidence);
        Assert.Equal(SubjectSource.CloudLlm, result.Source);
        Assert.Contains("Authorization", handler.LastRequest!.Headers.ToString());
        Assert.Contains("test-model", handler.LastBody);
        Assert.Contains("默写", handler.LastBody);
        Assert.Equal(1, cloud.TodayCallCount);
    }

    [Fact]
    public async Task 云端识别_HTTP错误或坏JSON_返回null不抛出()
    {
        var provider = CreateCloudProvider();
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.TooManyRequests, """{"error":"rate"}"""));
        var cloud = new CloudOpenAiProvider(provider, new HttpClient(handler), new XunitLogger(_output));

        var r1 = await cloud.ClassifyAsync("任意文本");
        Assert.Null(r1);

        var handler2 = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"不是JSON"}}]}"""));
        var cloud2 = new CloudOpenAiProvider(provider, new HttpClient(handler2), new XunitLogger(_output));
        var r2 = await cloud2.ClassifyAsync("任意文本");
        Assert.Null(r2);
    }

    [Fact]
    public async Task 云端每日限额_超限后IsAvailable关闭()
    {
        var provider = CreateCloudProvider(dailyLimit: 2);
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{\"subject\":\"数学\",\"confidence\":0.9,\"reason\":\"ok\"}"}}]}
            """));
        var cloud = new CloudOpenAiProvider(provider, new HttpClient(handler), new XunitLogger(_output));

        Assert.NotNull(await cloud.ClassifyAsync("第一次"));
        Assert.NotNull(await cloud.ClassifyAsync("第二次"));
        Assert.Equal(2, cloud.TodayCallCount);
        Assert.False(cloud.IsAvailable, "达到每日限额后应自动不可用（降级保护）");
        Assert.Null(await cloud.ClassifyAsync("第三次（不应发出请求）"));
    }

    [Fact]
    public void 云端未配置密钥或端点_不可用()
    {
        var provider = CreateCloudProvider(unprotector: _ => "");
        var cloud = new CloudOpenAiProvider(provider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("不应发起请求"))));
        Assert.False(cloud.IsAvailable);

        AiSettings noEndpoint = new() { CloudEndpoint = "" };
        var provider2 = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => noEndpoint,
            DataDirectory = _dataDir
        };
        Assert.False(new CloudOpenAiProvider(provider2).IsAvailable);
    }

    [Fact]
    public void 端点URL_自动补chatCompletions后缀()
    {
        Assert.Equal("https://a.com/v1/chat/completions", CloudOpenAiProvider.BuildEndpointUrl("https://a.com/v1"));
        Assert.Equal("https://a.com/v1/chat/completions",
            CloudOpenAiProvider.BuildEndpointUrl("https://a.com/v1/chat/completions/"));
    }

    // ---------- subjects.json 外置词表 ----------

    [Fact]
    public async Task 词表_数据目录缺失时从Assets模板种子()
    {
        var provider = CreateProvider();
        var keyword = CreateKeyword(provider);
        var dataPath = Path.Combine(_dataDir, "subjects.json");

        Assert.True(File.Exists(dataPath), "应从 Assets/subjects.json 种子到数据目录");
        Assert.True(keyword.IsAvailable);

        var result = await keyword.ClassifyAsync("完成古诗词背诵");
        Assert.NotNull(result);
        Assert.Equal("语文", result.Subject);

        // 热重载：修改词表后生效
        File.WriteAllText(dataPath, """
            { "rules": [ { "subject": "信息技术", "keywords": [ "编程" ], "priority": 5 } ] }
            """);
        keyword.ReloadRules();
        var after = await keyword.ClassifyAsync("今晚完成编程作业");
        Assert.NotNull(after);
        Assert.Equal("信息技术", after.Subject);
    }

    [Fact]
    public async Task 词表损坏_关键词级返回null_链不中断()
    {
        var provider = CreateProvider();
        File.WriteAllText(Path.Combine(_dataDir, "subjects.json"), "{ broken !!!");
        var keyword = CreateKeyword(provider);
        var store = new JsonPendingConfirmStore(provider);
        var fakeAi = new LocalFakeAiProvider(Result("数学", 0.9, SubjectSource.LocalModel));
        var chain = new SubjectClassifierChain(keyword, new IAiProvider[] { fakeAi },
            store, provider, new XunitLogger(_output));

        var result = await chain.ClassifyAsync("解一元二次方程", "msg-broken-rules");

        Assert.Equal("数学", result.Subject);
        Assert.False(result.NeedsManualConfirm);
    }
}
