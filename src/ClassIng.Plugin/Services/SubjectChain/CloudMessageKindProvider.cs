using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>「通知/作业二分类」用途的 AI 识别结果。</summary>
public sealed record MessageKindVerdict(MessageKind Kind, double Confidence, string Reason);

/// <summary>
/// 「通知/作业二分类」用途的 AI 识别提供者抽象（需求 6 用途②）。
/// 与 <see cref="IAiProvider"/>（学科判定）分开：输出是消息种类而非学科。
/// </summary>
public interface IMessageKindAiProvider
{
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>判定消息是通知还是作业；超时/失败/不可用返回 null（调用方降级回关键词链路），不抛异常。</summary>
    Task<MessageKindVerdict?> ClassifyAsync(string text, CancellationToken ct = default);
}

internal sealed class MessageKindVerdictDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>
/// 「通知/作业二分类」用途的云端 OpenAI 兼容识别器（需求 6 用途②）。
/// <para>
/// 纪律与 <see cref="CloudOpenAiProvider"/> 一致：HttpClient 超时 20 秒、单次尝试无链内重试、
/// 异常吞掉返回 null 降级；每日调用限额复用 <see cref="AiSettings.CloudDailyCallLimit"/>
/// （本提供者独立计数，与学科识别的云端提供者各算一份）；密钥绝不写入日志。
/// 设置来源 AiSettings；CloudProvider != OpenAiCompatible 时不可用（Anthropic 由
/// <see cref="AnthropicMessageKindProvider"/> 接管，经 CompositeMessageKindProvider 选择）。
/// </para>
/// </summary>
public sealed class CloudMessageKindProvider : IMessageKindAiProvider
{
    private const string SystemPrompt =
        "你是中小学群消息分类器。判断用户消息是「通知」还是「作业」：" +
        "作业=要求学生完成/提交/练习的内容；通知=告知、提醒、广播类信息。" +
        "只输出 JSON：{\"kind\":\"通知\"|\"作业\",\"confidence\":<0到1的小数>,\"reason\":\"<简短理由>\"}，不要输出其他内容。";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly object _countLock = new();

    public CloudMessageKindProvider(
        SubjectChainOptionsProvider provider,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc />
    public string Name => "CloudMessageKind";

    /// <summary>今日已调用次数（跨日自动清零）。</summary>
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
                || settings.CloudProvider != CloudAiProvider.OpenAiCompatible
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

            using var request = new HttpRequestMessage(HttpMethod.Post, CloudOpenAiProvider.BuildEndpointUrl(settings.CloudEndpoint));
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
                    temperature = Math.Clamp(_provider.SafeGetAiSettings().CloudTemperature, 0, 2)
                }, RequestJsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("消息二分类 AI HTTP {Status}，返回 null 降级回关键词链路", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var completion = JsonSerializer.Deserialize<ChatCompletionResponseDto>(body, JsonOptions);
            var content = completion?.Choices.FirstOrDefault()?.Message?.Content ?? "";
            var verdict = ParseVerdict(content);
            if (verdict is null)
            {
                _logger.LogWarning("消息二分类 AI 返回无法解析：{Content}", content);
                return null;
            }

            _logger.LogInformation("消息二分类 AI 判定 {Kind}（置信度 {Confidence}）",
                verdict.Kind, verdict.Confidence);
            return verdict;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息二分类 AI 异常，返回 null 降级回关键词链路");
            return null;
        }
    }

    /// <summary>从 LLM 文本中提取消息种类判定 JSON（容忍 ```json 围栏等噪音）。</summary>
    internal static MessageKindVerdict? ParseVerdict(string content)
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
            var dto = JsonSerializer.Deserialize<MessageKindVerdictDto>(json, JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Kind))
            {
                return null;
            }

            var kind = dto.Kind.Trim() switch
            {
                "作业" => MessageKind.Homework,
                "通知" => MessageKind.Notice,
                _ => MessageKind.Unknown
            };
            if (kind == MessageKind.Unknown)
            {
                return null;
            }

            return new MessageKindVerdict(kind, Math.Clamp(dto.Confidence, 0, 1),
                string.IsNullOrWhiteSpace(dto.Reason) ? "no_reason" : dto.Reason);
        }
        catch (JsonException)
        {
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
