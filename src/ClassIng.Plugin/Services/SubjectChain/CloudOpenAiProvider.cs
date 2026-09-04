using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>OpenAI 兼容 chat/completions 响应的最小 DTO（仅取所需字段）。</summary>
internal sealed class ChatCompletionResponseDto
{
    [JsonPropertyName("choices")]
    public List<ChatChoiceDto> Choices { get; set; } = [];
}

internal sealed class ChatChoiceDto
{
    [JsonPropertyName("message")]
    public ChatMessageDto? Message { get; set; }
}

internal sealed class ChatMessageDto
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>LLM 返回的学科判定 JSON（要求模型输出 {"subject","confidence","reason"}）。</summary>
internal sealed class LlmSubjectVerdictDto
{
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>
/// 模块 3 第②级（云端）：OpenAI 兼容 API 学科识别器（HttpClient 注入，可测）。
/// 含每日调用限额保护（超过 <see cref="ClassificationSettings.CloudDailyCallLimit"/> 后
/// <see cref="IsAvailable"/>=false 自动降级到人工队列）。
/// </summary>
public sealed class CloudOpenAiProvider : IAiProvider
{
    private const string SystemPrompt =
        "你是中小学作业学科分类器。根据用户文本判断学科（数学/语文/英语/物理/化学/生物/历史/地理/政治）。" +
        "只输出 JSON：{\"subject\":\"<学科>\",\"confidence\":<0到1的小数>,\"reason\":\"<简短理由>\"}，不要输出其他内容。";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>请求序列化：不转义非 ASCII（中文原文直出，部分兼容端点对 \u 转义支持差）。</summary>
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly object _countLock = new();

    public CloudOpenAiProvider(
        SubjectChainOptionsProvider provider,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc />
    public string Name => "CloudLlm";

    /// <summary>今日已调用次数（跨日自动清零；排错面板可读）。</summary>
    public int TodayCallCount { get; private set; }

    /// <summary>计数所属日期（本地时区）。</summary>
    public DateOnly CountDate { get; private set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            var settings = _provider.SafeGetSettings();
            if (!settings.AiEnabled || string.IsNullOrWhiteSpace(settings.CloudEndpoint))
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

            var settings = _provider.SafeGetSettings();
            if (!IsAvailable || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var apiKey = _provider.SecretUnprotector(settings.CloudApiKeyProtected);
            IncrementCount();

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUrl(settings.CloudEndpoint));
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = settings.CloudModelName,
                    messages = new object[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = text }
                    },
                    temperature = 0
                }, RequestJsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("云端识别 HTTP {Status}，返回 null 进入下一级", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var completion = JsonSerializer.Deserialize<ChatCompletionResponseDto>(body, JsonOptions);
            var content = completion?.Choices.FirstOrDefault()?.Message?.Content ?? "";
            var verdict = ParseVerdict(content);
            if (verdict is null || string.IsNullOrWhiteSpace(verdict.Subject))
            {
                _logger.LogWarning("云端返回无法解析为学科判定：{Content}", content);
                return null;
            }

            var confidence = Math.Clamp(verdict.Confidence, 0, 1);
            _logger.LogInformation("云端识别学科 {Subject}（置信度 {Confidence}）",
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
            _logger.LogError(ex, "云端识别异常，返回 null 进入下一级");
            return null;
        }
    }

    /// <summary>从 LLM 文本中提取学科判定 JSON（容忍 ```json 围栏等噪音）。</summary>
    internal static LlmSubjectVerdictDto? ParseVerdict(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var json = content.Trim();
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        json = json[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize<LlmSubjectVerdictDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string BuildEndpointUrl(string endpoint)
    {
        var url = endpoint.Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
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
