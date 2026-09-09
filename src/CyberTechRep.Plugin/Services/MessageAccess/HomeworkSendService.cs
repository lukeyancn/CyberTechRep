using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>发送服务依赖提供者（解耦设置服务，便于单元测试）。</summary>
public sealed class HomeworkSendOptionsProvider
{
    /// <summary>提供当前连接设置（AppId/AppSecret/ApiBase/目标群列表；设置页修改后热生效）。</summary>
    public required Func<ConnectionSettings> GetSettings { get; init; }

    /// <summary>解密 AppSecretProtected → 明文（默认直接返回原值，供测试使用；宿主注入 DPAPI 实现）。</summary>
    public Func<string, string> SecretUnprotector { get; init; } = v => v;

    /// <summary>
    /// 发送功能总开关（防误发；null/true = 开启）。接线 ConnectionSettings.HomeworkSendEnabled（默认 true）。
    /// </summary>
    public Func<bool>? GetEnabled { get; init; }

    /// <summary>
    /// 当前接入模式（null = Official）。NapCat 模式发送走 OneBot <c>send_group_msg</c>，
    /// 不请求 AccessToken——这正是「NapCat 模式下报 AccessToken 缺字段」的根因之一。
    /// </summary>
    public Func<MessageConnectionMode>? GetMode { get; init; }

    /// <summary>
    /// 协议端原生 API 调用（NapCat 模式发送用；官方模式为 null）。
    /// 接线 <c>IMessageIngestService.CallProtocolApiAsync</c>。
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<JsonElement?>>? CallProtocolApi { get; init; }

    /// <summary>HTTP 调用器（默认内部 HttpClient；单元测试注入本地测试服务器）。</summary>
    public HttpMessageInvoker? HttpInvoker { get; set; }
}

/// <summary>
/// 作业清单整理并发送服务（QQ 官方机器人开放平台群消息 REST API）。
/// <para>
/// 发送目标 = 连接设置 TargetGroupOpenIds（作业清单发送目标群，群 OpenID），
/// 与消息接管白名单 GroupWhitelist 相互独立；多群逐一发送并逐群汇总结果；
/// 单群失败（HTTP 错误 / 平台业务错误码 / 网络异常）不中断其余群，失败原因原样带回 UI（不静默）。
/// </para>
/// <para>
/// AccessToken 体系与 <see cref="QQOfficialWsClient"/> 相同：POST TokenApiUrl
/// （JSON {appId, clientSecret}）→ access_token（默认 7200s，提前 5 分钟刷新），
/// 请求头 Authorization: QQBot &lt;token&gt;。
/// </para>
/// <para>
/// 群消息发送：POST {ApiBase}/v2/groups/{group_openid}/messages，msg_type=0（纯文本）。
/// 主动消息（默认，不带 msg_id）受平台频控（未认证 30/qpm、认证 60/qpm，单群每日上限 1000 条，
/// 见 docs/research/protocol-risk-free-options.md §2.3），频控失败以平台错误码明确报错回 UI；
/// 传入 msgId 则按被动回复发送（来源消息 5 分钟内有效，同 msg_id 最多回复 5 次，msg_seq 区分多次）。
/// </para>
/// </summary>
public sealed class HomeworkSendService : IHomeworkSendService
{
    private readonly HomeworkSendOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // --- AccessToken 缓存（与 WsClient 各自独立缓存，互不影响）---
    private readonly object _tokenLock = new();
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public HomeworkSendService(HomeworkSendOptionsProvider provider, ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>发送功能是否开启（开关接线连接设置 HomeworkSendEnabled，默认开）。</summary>
    public bool IsEnabled => _provider.GetEnabled?.Invoke() ?? true;

    public async Task<IReadOnlyList<GroupSendResult>> SendTextToTargetGroupsAsync(
        string content, string? msgId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);

        if (!IsEnabled)
        {
            _logger.LogWarning("作业清单发送被开关禁用，未发送");
            return [new GroupSendResult("", false, "发送功能已关闭")];
        }

        var settings = _provider.GetSettings();
        var groups = settings.TargetGroupOpenIds
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0)
        {
            _logger.LogWarning("发送目标群列表为空，作业清单未发送");
            return [new GroupSendResult("", false, "发送目标群为空（连接设置中未配置目标群，与消息接管白名单相互独立），未发送")];
        }

        var results = new List<GroupSendResult>(groups.Count);
        var total = System.Diagnostics.Stopwatch.StartNew();
        // 逐群串行发送（信号量串行化，防并发触发平台 qpm 频控）
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var group in groups)
            {
                var groupWatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await SendToGroupAsync(settings, group, content, msgId, ct).ConfigureAwait(false);
                    groupWatch.Stop();
                    results.Add(new GroupSendResult(group, true, null));
                    _logger.LogInformation(
                        "作业清单已发送到群 Group={Group}, ElapsedMs={ElapsedMs}", group, groupWatch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单群失败不中断其余群；失败原因带回 UI（不静默）
                    groupWatch.Stop();
                    _logger.LogWarning(ex,
                        "作业清单发送失败 Group={Group}, ElapsedMs={ElapsedMs}", group, groupWatch.ElapsedMilliseconds);
                    results.Add(new GroupSendResult(group, false, ex.Message));
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }

        total.Stop();
        var ok = results.Count(r => r.Success);
        _logger.LogInformation(
            "作业清单发送汇总：Total={Total}, Success={Success}, Failed={Failed}, ElapsedMs={ElapsedMs}",
            results.Count, ok, results.Count - ok, total.ElapsedMilliseconds);

        return results;
    }

    /// <summary>向单个群发送文本消息（失败抛异常，由调用方捕获归入该群结果）。</summary>
    private async Task SendToGroupAsync(
        ConnectionSettings settings, string groupOpenId, string content, string? msgId, CancellationToken ct)
    {
        // NapCat（OneBot 11）模式：经当前 WS 连接调 send_group_msg，不请求 AccessToken、不走官方 REST。
        // 修复根因之二：旧实现无论哪种模式都打官方 Token 接口，NapCat 模式下必然报「缺少 access_token」。
        if (settings.Mode == MessageConnectionMode.NapCat)
        {
            await SendToNapCatGroupAsync(settings, groupOpenId, content, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.AppId)
            || string.IsNullOrWhiteSpace(settings.AppSecretProtected))
        {
            throw new InvalidOperationException("机器人未配置 AppId/AppSecret（连接设置），无法发送");
        }

        var token = await EnsureTokenAsync(settings, ct).ConfigureAwait(false);
        var url = $"{settings.ApiBase.TrimEnd('/')}/v2/groups/{Uri.EscapeDataString(groupOpenId)}/messages";

        // msg_type=0 纯文本；主动消息不带 msg_id（受频控），被动回复带 msg_id + msg_seq（1~5 区分多次回复）
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["msg_type"] = 0,
            ["msg_seq"] = 1
        };
        if (!string.IsNullOrWhiteSpace(msgId))
        {
            payload["msg_id"] = msgId;
        }

        var invoker = _provider.HttpInvoker ?? CreateDefaultHttpInvoker();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var resp = await invoker.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"群 {groupOpenId} 发送失败：HTTP {(int)resp.StatusCode} {DescribePlatformError(body)}");
        }

        // 官方接口业务错误可能随 HTTP 200 返回 {"code": !=0, "message": ...}，需解析兜底
        var code = TryGetField(body, "code", out var codeText) && int.TryParse(codeText, out var c) ? c : 0;
        if (code != 0)
        {
            throw new InvalidOperationException($"群 {groupOpenId} 发送失败：平台错误码 {code} {DescribePlatformError(body)}");
        }
    }

    /// <summary>
    /// NapCat（OneBot 11）发送：<c>send_group_msg</c>，目标群按 NapCat 群号传入。
    /// <para>
    /// 说明：NapCat 的 <c>group_id</c> 是数字群号，与官方平台的群 OpenID 不是同一命名空间——
    /// 因此 NapCat 模式下「整理并发送」目标群设置里要填群号（设置页已写明）。
    /// 传了 OpenID 形态的串时直接给出可操作的错误提示，而不是让 NapCat 静默失败。
    /// </para>
    /// </summary>
    private async Task SendToNapCatGroupAsync(
        ConnectionSettings settings, string groupId, string content, CancellationToken ct)
    {
        var api = _provider.CallProtocolApi;
        if (api is null)
        {
            throw new InvalidOperationException(
                "NapCat 模式发送需要协议端连接（消息接入服务未启动或未接线 CallProtocolApi）");
        }

        if (!long.TryParse(groupId, out _))
        {
            throw new InvalidOperationException(
                $"NapCat 模式的发送目标必须是 QQ 群号（纯数字），当前值「{groupId}」不是群号。" +
                "请在「连接设置 → 作业清单发送目标群」中填写群号（与官方模式的群 OpenID 不同）。");
        }

        var parameters = new Dictionary<string, object?>
        {
            ["group_id"] = groupId,
            ["message"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["data"] = new Dictionary<string, object?> { ["text"] = content }
                }
            }
        };

        _logger.LogInformation("NapCat 模式发送作业清单：group={Group}", groupId);
        var data = await api("send_group_msg", parameters, ct).ConfigureAwait(false);
        if (data is null)
        {
            // CallApiAsync 契约：连接不可用/超时/retcode≠0 一律返回 null（错误细节在排错面板日志里）
            throw new InvalidOperationException(
                $"群 {groupId} 发送失败：NapCat 未返回成功结果（连接断开 / 超时 / retcode≠0）。" +
                "请在「维护 → NapCat 日志」中查看具体原因。");
        }

        _logger.LogInformation("NapCat 发送成功：group={Group}, message_id={MessageId}",
            groupId, data.Value.ValueKind == JsonValueKind.Object
                && data.Value.TryGetProperty("message_id", out var mid) ? mid.ToString() : "(未返回)");
    }

    /// <summary>
    /// 获取缓存的 AccessToken（过期前 5 分钟自动刷新；逻辑与 WsClient 一致）。
    /// <para>
    /// 兼容解析 + 诊断：见 <see cref="AccessTokenResponseParser"/>。失败时异常消息内含
    /// <b>脱敏后的原始响应片段</b>与协议端错误码，同时写入结构化日志（便于排错面板定位）。
    /// </para>
    /// </summary>
    private async Task<string> EnsureTokenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        lock (_tokenLock)
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            {
                return _accessToken;
            }
        }

        var secret = _provider.SecretUnprotector(settings.AppSecretProtected);
        var invoker = _provider.HttpInvoker ?? CreateDefaultHttpInvoker();
        _logger.LogInformation("请求 AccessToken（发送用）：{Url}（AppId={AppId}）", settings.TokenApiUrl, MaskAppId(settings.AppId));

        using var req = new HttpRequestMessage(HttpMethod.Post, settings.TokenApiUrl);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { appId = settings.AppId, clientSecret = secret }),
            Encoding.UTF8,
            "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var resp = await invoker.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // HTTP 非 2xx：带状态码 + 脱敏响应片段（此前只输出状态码，用户无从判断是 AppId 错还是网络错）
        if (!resp.IsSuccessStatusCode)
        {
            var httpDiagnostic =
                $"AccessToken 获取失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}；" +
                $"响应片段：{AccessTokenResponseParser.SanitizeSnippet(body)}";
            _logger.LogError("AccessToken 请求失败（发送用）：HTTP {Status}, Url={Url}, 响应片段={Snippet}",
                (int)resp.StatusCode, settings.TokenApiUrl, AccessTokenResponseParser.SanitizeSnippet(body));
            throw new InvalidOperationException(httpDiagnostic);
        }

        var parsed = AccessTokenResponseParser.Parse(body);
        if (!parsed.Success)
        {
            _logger.LogError("AccessToken 响应解析失败（发送用）：{Diagnostic}（Url={Url}, AppId={AppId}）",
                parsed.Diagnostic, settings.TokenApiUrl, MaskAppId(settings.AppId));
            throw new InvalidOperationException($"AccessToken 解析失败：{parsed.Diagnostic}");
        }

        lock (_tokenLock)
        {
            _accessToken = parsed.Token;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(parsed.ExpiresIn - 300, 60));
        }

        _logger.LogInformation("AccessToken 获取成功（发送用），有效期 {Seconds}s", parsed.ExpiresIn);
        return parsed.Token;
    }

    /// <summary>从平台响应提取错误说明（code/message），截断防日志膨胀。</summary>
    private static string DescribePlatformError(string body)
    {
        var message = TryGetField(body, "message", out var m) ? m : body;
        message = message.Length > 200 ? message[..200] + "…" : message;
        // 频控等业务错误以平台错误码呈现（如 11243/11253），带上码值便于对照官方文档排查
        return TryGetField(body, "code", out var code) ? $"code={code} {message}" : message;
    }

    /// <summary>顶层 JSON 字段宽松提取（值为字符串或数字）。</summary>
    private static bool TryGetField(string json, string field, out string value)
    {
        value = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var el))
            {
                value = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
                return true;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应体：交由调用方原样使用
        }

        return false;
    }

    private static HttpMessageInvoker CreateDefaultHttpInvoker()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        return new HttpMessageInvoker(handler);
    }

    /// <summary>日志脱敏：AppId 只保留前 4 位，避免完整标识符进入日志（与 WsClient/Ingest 同规则）。</summary>
    private static string MaskAppId(string appId)
        => string.IsNullOrEmpty(appId) ? "(空)"
            : appId.Length <= 4 ? appId : appId[..4] + "***";
}
