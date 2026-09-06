using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.SubjectChain;

/// <summary>Anthropic Messages API 响应的最小 DTO（仅取所需字段）。</summary>
internal sealed class AnthropicMessagesResponseDto
{
    [JsonPropertyName("content")]
    public List<AnthropicContentBlockDto> Content { get; set; } = [];
}

internal sealed class AnthropicContentBlockDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

/// <summary>
/// Anthropic Messages API 的请求构造与响应解析（需求 5+6，OpenAI 兼容路径之外的第二个云端提供者）。
/// <para>
/// 端点：<c>{base}/v1/messages</c>；鉴权头 <c>x-api-key</c> + <c>anthropic-version</c>；
/// 请求体 messages/system/max_tokens；响应取 content 中 type=text 的文本拼接。
/// 密钥只进入请求头，绝不写入日志（脱敏口径：只记状态码）。
/// </para>
/// </summary>
internal static class AnthropicApi
{
    /// <summary>Anthropic 版本头（Messages API 稳定版本）。</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>分类任务输出很短，max_tokens 固定即可。</summary>
    public const int MaxTokens = 1024;

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>端点规范化：自动补 /v1/messages 后缀（已带则原样返回）。</summary>
    public static string BuildEndpointUrl(string endpoint)
    {
        var url = endpoint.Trim().TrimEnd('/');
        return url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/v1/messages";
    }

    /// <summary>构造 Messages API 请求（system 提示 + 单条 user 消息 + 采样温度）。</summary>
    public static HttpRequestMessage CreateRequest(
        string endpoint, string apiKey, string model, string systemPrompt, string userText, double temperature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUrl(endpoint));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                max_tokens = MaxTokens,
                system = systemPrompt,
                messages = new object[]
                {
                    new { role = "user", content = userText }
                },
                temperature = Math.Clamp(temperature, 0, 2)
            }, CloudOpenAiProvider.RequestJsonOptions),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    /// <summary>从响应 JSON 提取 assistant 文本（拼接全部 type=text 块）；解析失败返回空串。</summary>
    public static string ExtractContent(string responseBody)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<AnthropicMessagesResponseDto>(responseBody, ResponseJsonOptions);
            return string.Concat(
                (dto?.Content ?? []).Where(b => b.Type == "text").Select(b => b.Text));
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

/// <summary>
/// 模块 3 第②级（云端）：Anthropic Messages API 学科识别器（需求 5+6）。
/// <para>
/// 纪律与 <see cref="CloudOpenAiProvider"/> 完全一致：HttpClient 超时 20 秒、异常吞掉返回 null
/// 降级、每日调用限额保护、密钥绝不入日志；判定 JSON 解析复用
/// <see cref="CloudOpenAiProvider.ParseVerdict"/>（同一学科判定契约）。
/// <see cref="AiSettings.CloudProvider"/> != <see cref="CloudAiProvider.Anthropic"/> 时不可用，
/// 链组合器自动跳过（路由层无感切换）。
/// </para>
/// </summary>
public sealed class AnthropicCloudProvider : IAiProvider
{
    private const string SystemPrompt =
        "你是中小学作业学科分类器。根据用户文本判断学科（数学/语文/英语/物理/化学/生物/历史/地理/政治）。" +
        "只输出 JSON：{\"subject\":\"<学科>\",\"confidence\":<0到1的小数>,\"reason\":\"<简短理由>\"}，不要输出其他内容。";

    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly object _countLock = new();

    public AnthropicCloudProvider(
        SubjectChainOptionsProvider provider,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc />
    public string Name => "AnthropicCloud";

    /// <summary>今日已调用次数（跨日自动清零；排错面板可读）。</summary>
    public int TodayCallCount { get; private set; }

    /// <summary>计数所属日期（本地时区）。</summary>
    public DateOnly CountDate { get; private set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            var settings = _provider.SafeGetAiSettings();
            if (!settings.AiEnabled
                || settings.CloudProvider != CloudAiProvider.Anthropic
                || string.IsNullOrWhiteSpace(settings.CloudEndpoint))
            {
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_provider.SecretUnprotector(settings.CloudApiKeyProtected)))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            RollDateIfNeeded();
            return TodayCallCount < Math.Max(1, settings.CloudDailyCallLimit);
        }
    }

    /// <inheritdoc />
    public async Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var settings = _provider.SafeGetAiSettings();
            if (!IsAvailable || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var apiKey = _provider.SecretUnprotector(settings.CloudApiKeyProtected);
            IncrementCount();

            using var request = AnthropicApi.CreateRequest(
                settings.CloudEndpoint, apiKey, settings.CloudModelName,
                SystemPrompt, text, _provider.SafeGetAiSettings().CloudTemperature);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic 云端识别 HTTP {Status}，返回 null 进入下一级", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var content = AnthropicApi.ExtractContent(body);
            var verdict = CloudOpenAiProvider.ParseVerdict(content);
            if (verdict is null || string.IsNullOrWhiteSpace(verdict.Subject))
            {
                _logger.LogWarning("Anthropic 返回无法解析为学科判定：{Content}", content);
                return null;
            }

            var confidence = Math.Clamp(verdict.Confidence, 0, 1);
            _logger.LogInformation("Anthropic 云端识别学科 {Subject}（置信度 {Confidence}）",
                verdict.Subject, confidence);
            return new SubjectResult
            {
                Subject = verdict.Subject.Trim(),
                Confidence = confidence,
                Source = SubjectSource.CloudLlm,
                Reason = "llm:" + (string.IsNullOrWhiteSpace(verdict.Reason) ? "no_reason" : verdict.Reason)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anthropic 云端识别异常，返回 null 进入下一级");
            return null;
        }
    }

    private void IncrementCount()
    {
        lock (_countLock)
        {
            RollDateIfNeeded();
            TodayCallCount++;
        }
    }

    private void RollDateIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (CountDate != today)
        {
            CountDate = today;
            TodayCallCount = 0;
        }
    }
}

/// <summary>
/// 「通知/作业二分类」用途的 Anthropic Messages API 识别器（需求 6 用途② + 需求 5+6 Provider 切换）。
/// 判定解析复用 <see cref="CloudMessageKindProvider.ParseVerdict"/>；其余纪律同
/// <see cref="AnthropicCloudProvider"/>。CloudProvider != Anthropic 时不可用。
/// </summary>
public sealed class AnthropicMessageKindProvider : IMessageKindAiProvider
{
    private const string SystemPrompt =
        "你是中小学群消息分类器。判断用户消息是「通知」还是「作业」：" +
        "作业=要求学生完成/提交/练习的内容；通知=告知、提醒、广播类信息。" +
        "只输出 JSON：{\"kind\":\"通知\"|\"作业\",\"confidence\":<0到1的小数>,\"reason\":\"<简短理由>\"}，不要输出其他内容。";

    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly object _countLock = new();

    public AnthropicMessageKindProvider(
        SubjectChainOptionsProvider provider,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc />
    public string Name => "AnthropicMessageKind";

    /// <summary>今日已调用次数（跨日自动清零；独立计数）。</summary>
    public int TodayCallCount { get; private set; }

    /// <summary>计数所属日期（本地时区）。</summary>
    public DateOnly CountDate { get; private set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            var settings = _provider.SafeGetAiSettings();
            if (!settings.AiEnabled
                || settings.CloudProvider != CloudAiProvider.Anthropic
                || string.IsNullOrWhiteSpace(settings.CloudEndpoint))
            {
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_provider.SecretUnprotector(settings.CloudApiKeyProtected)))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            RollDateIfNeeded();
            return TodayCallCount < Math.Max(1, settings.CloudDailyCallLimit);
        }
    }

    /// <inheritdoc />
    public async Task<MessageKindVerdict?> ClassifyAsync(string text, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var settings = _provider.SafeGetAiSettings();
            if (!IsAvailable || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var apiKey = _provider.SecretUnprotector(settings.CloudApiKeyProtected);
            IncrementCount();

            using var request = AnthropicApi.CreateRequest(
                settings.CloudEndpoint, apiKey, settings.CloudModelName,
                SystemPrompt, text, _provider.SafeGetAiSettings().CloudTemperature);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("消息二分类 Anthropic HTTP {Status}，返回 null 降级回关键词链路", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var content = AnthropicApi.ExtractContent(body);
            var verdict = CloudMessageKindProvider.ParseVerdict(content);
            if (verdict is null)
            {
                _logger.LogWarning("消息二分类 Anthropic 返回无法解析：{Content}", content);
                return null;
            }

            _logger.LogInformation("消息二分类 Anthropic 判定 {Kind}（置信度 {Confidence}）",
                verdict.Kind, verdict.Confidence);
            return verdict;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息二分类 Anthropic 异常，返回 null 降级回关键词链路");
            return null;
        }
    }

    private void IncrementCount()
    {
        lock (_countLock)
        {
            RollDateIfNeeded();
            TodayCallCount++;
        }
    }

    private void RollDateIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (CountDate != today)
        {
            CountDate = today;
            TodayCallCount = 0;
        }
    }
}

/// <summary>
/// 二分类 AI 提供者选择器：按 <see cref="AiSettings.CloudProvider"/> 在 OpenAI 兼容与
/// Anthropic 提供者之间选择（两个实现各自按 Provider 类型门控 IsAvailable）。
/// <see cref="AiMessageKindRouter"/> 只依赖 <see cref="IMessageKindAiProvider"/> 单实例，路由层无感切换。
/// </summary>
public sealed class CompositeMessageKindProvider : IMessageKindAiProvider
{
    private readonly IReadOnlyList<IMessageKindAiProvider> _providers;

    public CompositeMessageKindProvider(params IMessageKindAiProvider[] providers)
    {
        _providers = providers ?? [];
    }

    /// <summary>当前生效的提供者名（排错可读）；无可用提供者时为 None。</summary>
    public string Name => _providers.FirstOrDefault(p => p.IsAvailable)?.Name ?? "None";

    /// <summary>任一底层提供者可用即可用。</summary>
    public bool IsAvailable => _providers.Any(p => p.IsAvailable);

    /// <summary>取第一个可用提供者执行；均不可用返回 null（调用方降级回关键词链路）。</summary>
    public async Task<MessageKindVerdict?> ClassifyAsync(string text, CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable)
            {
                continue;
            }

            return await provider.ClassifyAsync(text, ct);
        }

        return null;
    }
}
