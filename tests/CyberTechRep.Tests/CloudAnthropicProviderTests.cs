using System.Net;
using System.Text;
using System.Text.Json;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 5+6 测试：Anthropic Messages API 云端提供者（请求构造/响应解析/HTTP 层 mock）、
/// Provider 类型切换路由（OpenAI 兼容 ↔ Anthropic 无感切换）、AI 设置旧字段→新位置迁移兼容。
/// </summary>
public class CloudAnthropicProviderTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ITestOutputHelper _output;

    public CloudAnthropicProviderTests(ITestOutputHelper output)
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

    // ---------- 测试替身（与 SubjectChainTests 同款 HTTP mock） ----------

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

    private SubjectChainOptionsProvider CreateProvider(
        CloudAiProvider cloudProvider = CloudAiProvider.Anthropic,
        Func<string, string>? unprotector = null)
    {
        AiSettings ai = new()
        {
            CloudProvider = cloudProvider,
            CloudEndpoint = "https://api.anthropic-test.com",
            CloudApiKeyProtected = "protected-key",
            CloudModelName = "claude-test",
            CloudDailyCallLimit = 200
        };
        return new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => ai,
            DataDirectory = _dataDir,
            SecretUnprotector = unprotector ?? (_ => "plain-key")
        };
    }

    private const string SubjectOkResponse = """
        {
          "content": [
            { "type": "text", "text": "{\"subject\":\"英语\",\"confidence\":0.88,\"reason\":\"出现单词听写\"}" }
          ]
        }
        """;

    // ---------- Anthropic 学科识别（HTTP 层） ----------

    [Fact]
    public async Task Anthropic识别_请求头与请求体符合MessagesAPI()
    {
        var provider = CreateProvider();
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, SubjectOkResponse));
        var anthropic = new AnthropicCloudProvider(provider, new HttpClient(handler), new XunitLogger(_output));

        Assert.True(anthropic.IsAvailable);
        var result = await anthropic.ClassifyAsync("默写unit3单词并完成听力练习");

        Assert.NotNull(result);
        Assert.Equal("英语", result.Subject);
        Assert.Equal(0.88, result.Confidence);
        Assert.Equal(SubjectSource.CloudLlm, result.Source);

        // 请求头：x-api-key + anthropic-version（密钥不落地日志，此处仅验证请求构造）
        Assert.Equal("plain-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain("Authorization", handler.LastRequest.Headers.ToString());

        // 请求体：model / max_tokens / system / messages
        Assert.NotNull(handler.LastBody);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claude-test", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("max_tokens").GetInt32() > 0);
        Assert.Contains("学科分类器", body.RootElement.GetProperty("system").GetString());
        Assert.Equal("默写unit3单词并完成听力练习",
            body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(1, anthropic.TodayCallCount);
    }

    [Fact]
    public async Task Anthropic识别_多text块响应_拼接后解析()
    {
        var provider = CreateProvider();
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """
            {
              "content": [
                { "type": "text", "text": "{\"subject\":\"数" },
                { "type": "tool_use", "id": "x", "name": "t" },
                { "type": "text", "text":"学\",\"confidence\":0.9,\"reason\":\"ok\"}" }
              ]
            }
            """));
        var anthropic = new AnthropicCloudProvider(provider, new HttpClient(handler), new XunitLogger(_output));

        var result = await anthropic.ClassifyAsync("解一元二次方程");

        Assert.NotNull(result);
        Assert.Equal("数学", result.Subject);
        Assert.Equal(0.9, result.Confidence);
    }

    [Fact]
    public async Task Anthropic识别_HTTP错误或坏JSON_返回null不抛出()
    {
        var provider = CreateProvider();
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.TooManyRequests, """{"error":"rate"}"""));
        var anthropic = new AnthropicCloudProvider(provider, new HttpClient(handler), new XunitLogger(_output));
        Assert.Null(await anthropic.ClassifyAsync("任意文本"));

        var handler2 = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """{"content":[{"type":"text","text":"不是JSON"}]}"""));
        var anthropic2 = new AnthropicCloudProvider(provider, new HttpClient(handler2), new XunitLogger(_output));
        Assert.Null(await anthropic2.ClassifyAsync("任意文本"));
    }

    [Fact]
    public async Task Anthropic每日限额_超限后IsAvailable关闭()
    {
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, SubjectOkResponse));
        var anthropic = new AnthropicCloudProvider(
            CreateLimitedProvider(1), new HttpClient(handler), new XunitLogger(_output));

        Assert.True(anthropic.IsAvailable);
        Assert.NotNull(await anthropic.ClassifyAsync("第一次"));
        Assert.Equal(1, anthropic.TodayCallCount);
        Assert.False(anthropic.IsAvailable, "达到每日限额后应自动不可用（降级保护）");
        Assert.Null(await anthropic.ClassifyAsync("第二次（不应发出请求）"));
        Assert.Equal(1, handler.LastRequest is null ? 0 : 1);
    }

    private SubjectChainOptionsProvider CreateLimitedProvider(int limit)
    {
        var p = CreateProvider();
        var ai = new AiSettings
        {
            CloudProvider = CloudAiProvider.Anthropic,
            CloudEndpoint = "https://api.anthropic-test.com",
            CloudApiKeyProtected = "protected-key",
            CloudModelName = "claude-test",
            CloudDailyCallLimit = limit
        };
        return new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => ai,
            DataDirectory = _dataDir,
            SecretUnprotector = _ => "plain-key"
        };
    }

    [Fact]
    public void Anthropic端点URL_自动补v1Messages后缀()
    {
        Assert.Equal("https://a.com/v1/messages", AnthropicApi.BuildEndpointUrl("https://a.com"));
        Assert.Equal("https://a.com/v1/messages", AnthropicApi.BuildEndpointUrl("https://a.com/"));
        Assert.Equal("https://a.com/v1/messages", AnthropicApi.BuildEndpointUrl("https://a.com/v1/messages"));
    }

    // ---------- Provider 类型门控与路由切换 ----------

    [Fact]
    public void Provider门控_OpenAI提供者在Anthropic模式下不可用()
    {
        var provider = CreateProvider(cloudProvider: CloudAiProvider.Anthropic);
        var openAi = new CloudOpenAiProvider(provider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("Anthropic 模式下不应发起 OpenAI 请求"))));
        Assert.False(openAi.IsAvailable);
    }

    [Fact]
    public void Provider门控_Anthropic提供者在默认OpenAI模式下不可用()
    {
        var provider = CreateProvider(cloudProvider: CloudAiProvider.OpenAiCompatible);
        var anthropic = new AnthropicCloudProvider(provider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("OpenAI 模式下不应发起 Anthropic 请求"))));
        Assert.False(anthropic.IsAvailable);
    }

    [Fact]
    public async Task Provider切换_链路由按设置选择Anthropic而非OpenAI()
    {
        // 两个云端提供者都注册（与 Plugin.cs 一致）；CloudProvider=Anthropic 时链应只调用 Anthropic
        var provider = CreateProvider(cloudProvider: CloudAiProvider.Anthropic);
        var keyword = new KeywordSubjectClassifier(provider, new XunitLogger(_output));
        var handler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, SubjectOkResponse));
        var openAi = new CloudOpenAiProvider(provider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("Anthropic 模式下不应调用 OpenAI 端点"))), new XunitLogger(_output));
        var anthropic = new AnthropicCloudProvider(provider, new HttpClient(handler), new XunitLogger(_output));
        var chain = new SubjectClassifierChain(
            keyword, new IAiProvider[] { openAi, anthropic },
            new JsonPendingConfirmStore(provider), provider, new XunitLogger(_output));

        var result = await chain.ClassifyWithModeAsync("请研究滑轮组的机械效率", "msg-route-1", AiUsageMode.Backup);

        Assert.Equal("英语", result.Subject);
        Assert.Equal(1, anthropic.TodayCallCount);
    }

    [Fact]
    public async Task Provider切换_默认OpenAI兼容_链行为与迁移前一致()
    {
        var provider = CreateProvider(cloudProvider: CloudAiProvider.OpenAiCompatible);
        var keyword = new KeywordSubjectClassifier(provider, new XunitLogger(_output));
        var openAiHandler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{\"subject\":\"物理\",\"confidence\":0.95,\"reason\":\"ok\"}"}}]}
            """));
        var openAi = new CloudOpenAiProvider(provider, new HttpClient(openAiHandler), new XunitLogger(_output));
        var anthropic = new AnthropicCloudProvider(provider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("OpenAI 模式下不应调用 Anthropic 端点"))), new XunitLogger(_output));
        var chain = new SubjectClassifierChain(
            keyword, new IAiProvider[] { openAi, anthropic },
            new JsonPendingConfirmStore(provider), provider, new XunitLogger(_output));

        var result = await chain.ClassifyWithModeAsync("请推导天体轨道运动的参数", "msg-route-2", AiUsageMode.Backup);

        Assert.Equal("物理", result.Subject);
        Assert.Equal(1, openAi.TodayCallCount);
    }

    [Fact]
    public async Task 二分类Composite_按Provider设置选择底层提供者()
    {
        // Anthropic 模式：composite 应把请求发到 Anthropic 端点
        var anthropicProvider = CreateProvider(cloudProvider: CloudAiProvider.Anthropic);
        var anthropicHandler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """
            {"content":[{"type":"text","text":"{\"kind\":\"作业\",\"confidence\":0.9,\"reason\":\"要求提交\"}"}]}
            """));
        var anthropicKind = new AnthropicMessageKindProvider(
            anthropicProvider, new HttpClient(anthropicHandler), new XunitLogger(_output));
        var openAiKind = new CloudMessageKindProvider(anthropicProvider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("Anthropic 模式下不应调用 OpenAI 端点"))), new XunitLogger(_output));
        var composite = new CompositeMessageKindProvider(openAiKind, anthropicKind);

        Assert.True(composite.IsAvailable);
        Assert.Equal("AnthropicMessageKind", composite.Name);
        var verdict = await composite.ClassifyAsync("今晚完成数学练习册第3页并提交");
        Assert.NotNull(verdict);
        Assert.Equal(MessageKind.Homework, verdict.Kind);

        // OpenAI 兼容模式（默认）：composite 应走 OpenAI 提供者
        var openAiProvider = CreateProvider(cloudProvider: CloudAiProvider.OpenAiCompatible);
        var openAiHandler = new FakeHttpHandler(_ => Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{\"kind\":\"通知\",\"confidence\":0.9,\"reason\":\"广播\"}"}}]}
            """));
        var openAiKind2 = new CloudMessageKindProvider(
            openAiProvider, new HttpClient(openAiHandler), new XunitLogger(_output));
        var anthropicKind2 = new AnthropicMessageKindProvider(openAiProvider, new HttpClient(new FakeHttpHandler(
            _ => throw new InvalidOperationException("OpenAI 模式下不应调用 Anthropic 端点"))), new XunitLogger(_output));
        var composite2 = new CompositeMessageKindProvider(openAiKind2, anthropicKind2);

        Assert.True(composite2.IsAvailable);
        Assert.Equal("CloudMessageKind", composite2.Name);
        var verdict2 = await composite2.ClassifyAsync("明天上午广播体操比赛通知");
        Assert.NotNull(verdict2);
        Assert.Equal(MessageKind.Notice, verdict2.Kind);
    }

    [Fact]
    public void 二分类Composite_均不可用时不可用()
    {
        var provider = CreateProvider(cloudProvider: CloudAiProvider.Anthropic);
        // 未配置端点 → 两个底层都不可用
        var noneProvider = new SubjectChainOptionsProvider
        {
            GetSettings = () => new ClassificationSettings(),
            GetAiSettings = () => new AiSettings { AiEnabled = false },
            DataDirectory = _dataDir
        };
        var composite = new CompositeMessageKindProvider(
            new CloudMessageKindProvider(noneProvider),
            new AnthropicMessageKindProvider(noneProvider));

        Assert.False(composite.IsAvailable);
        Assert.Equal("None", composite.Name);
    }

    // ---------- 设置迁移兼容（旧字段 → 新位置） ----------

    [Fact]
    public void 迁移_旧位置字段复制到AiSettings后旧字段复位()
    {
        var settings = new AppSettings();
        settings.Classification.CloudEndpoint = "https://old.example.com/v1";
        settings.Classification.CloudApiKeyProtected = "old-protected-key";
        settings.Classification.CloudModelName = "old-model";
        settings.Classification.CloudDailyCallLimit = 50;
        settings.Classification.ConfidenceThreshold = 0.55;
        settings.Classification.AiEnabled = false;
        settings.Classification.PreferLocalModel = false;

        SettingsService.MigrateLegacyAiFields(settings);

        var ai = settings.Ai;
        Assert.Equal("https://old.example.com/v1", ai.CloudEndpoint);
        Assert.Equal("old-protected-key", ai.CloudApiKeyProtected);
        Assert.Equal("old-model", ai.CloudModelName);
        Assert.Equal(50, ai.CloudDailyCallLimit);
        Assert.Equal(0.55, ai.ConfidenceThreshold);
        Assert.False(ai.AiEnabled);
        Assert.False(ai.PreferLocalModel);

        // 旧字段复位为默认值（下次保存即从 settings.json 移除）
        Assert.Equal("", settings.Classification.CloudEndpoint);
        Assert.Equal("", settings.Classification.CloudApiKeyProtected);
        Assert.Equal("", settings.Classification.CloudModelName);
        Assert.Equal(200, settings.Classification.CloudDailyCallLimit);
        Assert.Equal(0.7, settings.Classification.ConfidenceThreshold);
        Assert.True(settings.Classification.AiEnabled);
        Assert.True(settings.Classification.PreferLocalModel);
    }

    [Fact]
    public async Task 加载旧版settingsJson_云端凭据迁移到新位置无需重填()
    {
        // 模拟升级前（0.2.x）的 settings.json：AI 字段全部在 Classification 下，无 Ai 段
        const string legacyJson = """
            {
              "SchemaVersion": 1,
              "Connection": {},
              "Classification": {
                "AiEnabled": true,
                "PreferLocalModel": false,
                "CloudEndpoint": "https://llm.old.com/v1",
                "CloudApiKeyProtected": "old-encrypted-key",
                "CloudModelName": "gpt-old",
                "CloudDailyCallLimit": 120,
                "ConfidenceThreshold": 0.6
              }
            }
            """;
        File.WriteAllText(Path.Combine(_dataDir, "settings.json"), legacyJson);

        var svc = new SettingsService(_dataDir);
        var ai = svc.Current.Ai;

        // 用户已配置的凭据自动迁移，无需重新填写
        Assert.Equal("https://llm.old.com/v1", ai.CloudEndpoint);
        Assert.Equal("old-encrypted-key", ai.CloudApiKeyProtected);
        Assert.Equal("gpt-old", ai.CloudModelName);
        Assert.Equal(120, ai.CloudDailyCallLimit);
        Assert.Equal(0.6, ai.ConfidenceThreshold);
        Assert.False(ai.PreferLocalModel);
        Assert.True(ai.AiEnabled);
        // 新字段默认值：OpenAI 兼容（现状行为不变）
        Assert.Equal(CloudAiProvider.OpenAiCompatible, ai.CloudProvider);
        // 旧字段已复位（迁移完成）
        Assert.Equal("", svc.Current.Classification.CloudEndpoint);

        // 往返：保存后重新加载，新位置字段持久化
        await svc.SaveAsync();
        var reloaded = new SettingsService(_dataDir);
        Assert.Equal("https://llm.old.com/v1", reloaded.Current.Ai.CloudEndpoint);
        Assert.Equal("old-encrypted-key", reloaded.Current.Ai.CloudApiKeyProtected);
        Assert.Equal("gpt-old", reloaded.Current.Ai.CloudModelName);
        Assert.Equal(120, reloaded.Current.Ai.CloudDailyCallLimit);
        Assert.Equal(CloudAiProvider.OpenAiCompatible, reloaded.Current.Ai.CloudProvider);
    }

    [Fact]
    public async Task 导入保留本机Ai密钥_不被导入覆盖()
    {
        var svc = new SettingsService(_dataDir);
        var localApiKey = svc.Protect("local-ai-key");
        svc.Current.Ai.CloudApiKeyProtected = localApiKey;
        svc.Current.Ai.CloudEndpoint = "https://mine.example.com";
        await svc.SaveAsync();

        var otherDir = Path.Combine(_dataDir, "other");
        Directory.CreateDirectory(otherDir);
        var other = new SettingsService(otherDir);
        other.Current.Ai.CloudApiKeyProtected = other.Protect("imported-key");
        other.Current.Ai.CloudEndpoint = "https://imported.example.com";
        var importJson = await other.ExportAsync();

        await svc.ImportAsync(importJson);

        // Secret 字段保留本地值（新位置）；非敏感字段随导入更新
        Assert.Equal(localApiKey, svc.Current.Ai.CloudApiKeyProtected);
        Assert.Equal("https://imported.example.com", svc.Current.Ai.CloudEndpoint);
        Assert.Equal("local-ai-key", svc.Unprotect(svc.Current.Ai.CloudApiKeyProtected));
    }

    [Fact]
    public async Task 导出不包含Ai密钥()
    {
        var svc = new SettingsService(_dataDir);
        svc.Current.Ai.CloudApiKeyProtected = svc.Protect("super-secret-ai-key");
        await svc.SaveAsync();

        var exported = await svc.ExportAsync();

        Assert.DoesNotContain("super-secret-ai-key", exported);
        var parsed = JsonSerializer.Deserialize<AppSettings>(exported);
        Assert.NotNull(parsed);
        Assert.Equal("", parsed!.Ai.CloudApiKeyProtected);
    }
}
