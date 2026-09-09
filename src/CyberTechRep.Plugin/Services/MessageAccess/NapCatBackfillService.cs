using System.Text.Json;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>
/// NapCat 消息断点续传·启动核对兜底（需求 4）。
/// <para>
/// <b>分工</b>：游标续传为主（<see cref="NapCatResumeCursorStore"/> 记录每群已入档到哪条），
/// 本服务是兜底——连接确认可用（收到生命周期/心跳）后，用 <c>get_group_msg_history</c>
/// 拉取每群最近 N 条，与游标核对，只把「游标之后」的消息注入接入管道补齐缺口。
/// </para>
/// <para>
/// <b>去重</b>：注入走既有管道（<c>MessageIdempotencyStore</c> 消息 id 幂等）+ 游标的
/// <c>IsCovered</c>（消息 id 优先，时间 + 内容哈希兜底），因此重复核对只补不重，
/// 悬浮窗与存档都不会出现重复条目。
/// </para>
/// <para>
/// <b>可容忍边界</b>：单群缺口超过核对窗口（<see cref="ConnectionSettings.NapCatBackfillCount"/>，
/// 默认 100 条）时无法补齐（插件长时间离线且群消息超过窗口）；调大窗口即可扩大兜底范围，
/// 代价是首次同步耗时线性增长。
/// </para>
/// <para>
/// <b>失败纪律</b>：单群/单好友失败只记日志不抛异常、不重试（下次连接或手动重连时再核对），
/// 绝不阻塞接收主循环。
/// </para>
/// </summary>
public sealed class NapCatBackfillService : IHostedService, IDisposable
{
    /// <summary>同一连接内的重复核对冷却（连接抖动/心跳重连时不重复拉取）。</summary>
    internal static readonly TimeSpan BackfillCooldown = TimeSpan.FromSeconds(30);

    private readonly MessageIngestService _ingest;
    private readonly NapCatResumeCursorStore _cursor;
    private readonly IngestOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRunAt = DateTimeOffset.MinValue;
    private volatile bool _disposed;

    public NapCatBackfillService(
        MessageIngestService ingest,
        NapCatResumeCursorStore cursor,
        IngestOptionsProvider provider,
        ILogger? logger = null)
    {
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>最近一次核对统计（排错面板/测试用）。</summary>
    public NapCatBackfillReport LastReport { get; private set; } = NapCatBackfillReport.Empty;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ingest.StatusChanged += OnStatusChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ingest.StatusChanged -= OnStatusChanged;
        await _cursor.FlushAsync().ConfigureAwait(false);
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status != ConnectionStatus.Connected || _disposed)
        {
            return;
        }

        _ = Task.Run(() => RunBackfillSafeAsync(CancellationToken.None));
    }

    /// <summary>核对入口（连接确认可用 / 手动重连后触发；internal 供测试直接驱动）。</summary>
    internal async Task RunBackfillSafeAsync(CancellationToken ct)
    {
        var settings = _provider.GetSettings();
        if (settings.Mode != MessageConnectionMode.NapCat || !settings.NapCatBackfillOnConnect)
        {
            return;
        }

        if (DateTimeOffset.Now - _lastRunAt < BackfillCooldown)
        {
            _logger.LogDebug("NapCat 启动核对在冷却窗口内跳过（距上次 {Seconds} 秒）",
                (DateTimeOffset.Now - _lastRunAt).TotalSeconds);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.Now - _lastRunAt < BackfillCooldown)
            {
                return;
            }

            _lastRunAt = DateTimeOffset.Now;
            var count = Math.Clamp(settings.NapCatBackfillCount, 1, 1000);
            var groups = 0;
            var injected = 0;
            var skipped = 0;

            foreach (var groupId in await ResolveGroupIdsAsync(settings, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var (accepted, dup) = await BackfillGroupAsync(groupId, count, ct).ConfigureAwait(false);
                if (accepted > 0 || dup > 0)
                {
                    groups++;
                }

                injected += accepted;
                skipped += dup;
            }

            await _cursor.FlushAsync().ConfigureAwait(false);
            LastReport = new NapCatBackfillReport(groups, injected, skipped, DateTimeOffset.Now);
            _logger.LogInformation(
                "NapCat 启动核对完成：核对群 {Groups} 个，补齐消息 {Injected} 条，去重跳过 {Skipped} 条（每群窗口 {Count} 条）",
                groups, injected, skipped, count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NapCat 启动核对已取消");
        }
        catch (Exception ex)
        {
            // 兜底核对失败不阻断接收：游标续传仍生效，下次连接再核对
            _logger.LogWarning(ex, "NapCat 启动核对异常（不影响实时接收，下次连接重试）");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 核对目标群：白名单非空时只核对白名单群；白名单为空（接收全部群）时经
    /// <c>get_group_list</c> 枚举当前已加入的全部群。
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveGroupIdsAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var whitelist = settings.GroupWhitelist
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (whitelist.Count > 0)
        {
            return whitelist;
        }

        var data = await _ingest.CallProtocolApiAsync("get_group_list", new Dictionary<string, object?>(), ct)
            .ConfigureAwait(false);
        var groups = new List<string>();
        if (data is { ValueKind: JsonValueKind.Array })
        {
            foreach (var item in data.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("group_id", out var id))
                {
                    var text = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "";
                    if (text.Length > 0)
                    {
                        groups.Add(text);
                    }
                }
            }
        }

        if (groups.Count == 0)
        {
            _logger.LogInformation("NapCat 启动核对：白名单为空且未能枚举群列表（get_group_list 无返回），本次无群可核对");
        }

        return groups;
    }

    /// <summary>核对单个群：拉最近 N 条 → 过滤游标覆盖 → 按时间正序注入管道。</summary>
    private async Task<(int Accepted, int Skipped)> BackfillGroupAsync(
        string groupId, int count, CancellationToken ct)
    {
        var data = await _ingest.CallProtocolApiAsync("get_group_msg_history", new Dictionary<string, object?>
        {
            ["group_id"] = groupId,
            ["count"] = count
        }, ct).ConfigureAwait(false);

        var messages = ExtractMessages(data);
        if (messages.Count == 0)
        {
            return (0, 0);
        }

        var accepted = 0;
        var skipped = 0;
        // 历史接口按时间倒序返回（最新在前）：正序注入，保证文档追加顺序与真实时间一致
        foreach (var item in messages.OrderBy(m => m.TimestampUnix))
        {
            ct.ThrowIfCancellationRequested();
            if (_cursor.IsCovered(groupId, item.MessageId, item.TimestampUnix, item.ContentHash))
            {
                skipped++;
                continue;
            }

            var result = _ingest.InjectHistoricalMessage(
                GroupEventTypes.GroupMessageCreate,
                item.MappedJson,
                item.ReceivedAt);
            if (result.Action == PipelineAction.Accepted)
            {
                accepted++;
                _cursor.Record(groupId, item.MessageId, item.TimestampUnix, item.ContentHash);
            }
            else
            {
                // 幂等存储已见过（重复消息）→ 只推进游标，不再显示/落档
                skipped++;
                _cursor.Record(groupId, item.MessageId, item.TimestampUnix, item.ContentHash);
            }
        }

        if (accepted > 0 || skipped > 0)
        {
            _logger.LogInformation(
                "NapCat 启动核对：群 {Group} 拉取 {Total} 条，补齐 {Accepted} 条，去重跳过 {Skipped} 条",
                groupId, messages.Count, accepted, skipped);
        }

        return (accepted, skipped);
    }

    /// <summary>历史条目 → 规范化后的官方事件 + 稳定键（解析失败跳过该条）。</summary>
    internal static IReadOnlyList<NapCatHistoryMessage> ExtractMessages(JsonElement? data)
    {
        var result = new List<NapCatHistoryMessage>();
        if (data is not { ValueKind: JsonValueKind.Object }
            || !data.Value.TryGetProperty("messages", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var messageId = GetIdText(item, "message_id");
            if (messageId.Length == 0)
            {
                continue;
            }

            long timestamp = 0;
            if (item.TryGetProperty("time", out var timeEl)
                && timeEl.ValueKind == JsonValueKind.Number
                && timeEl.TryGetInt64(out var seconds))
            {
                timestamp = seconds;
            }

            // 历史条目结构与消息事件几乎同构：补 post_type/message_type 后交给同一规范化器，
            // 保证「历史消息」与「实时消息」走完全相同的映射与下游管道（零分叉）。
            var eventJson = BuildEventJson(item, messageId);
            var normalized = NapCatEventNormalizer.Normalize(eventJson);
            if (!normalized.Handled || normalized.Recall is not null)
            {
                continue;
            }

            result.Add(new NapCatHistoryMessage(
                messageId,
                timestamp,
                timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime()
                    : DateTimeOffset.Now,
                normalized.MappedJson,
                // 稳定键的哈希输入必须与实时路径（RawJsonSnapshot = SanitizeRawSnapshot(事件JSON)）一致，
                // 否则同一条消息在「实时入档」与「重启核对」两条路径算出的键不相等，同秒兜底去重失效。
                NapCatResumeCursorStore.ContentHashKey(
                    timestamp, MessageIngestPipeline.SanitizeRawSnapshot(normalized.MappedJson))));
        }

        return result;
    }

    /// <summary>把历史条目补成标准消息事件 JSON（保留原始段结构与 sender）。</summary>
    private static string BuildEventJson(JsonElement item, string messageId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["post_type"] = "message",
            ["message_type"] = "group",
            ["message_id"] = messageId
        };

        foreach (var name in new[] { "group_id", "user_id", "time", "sender", "message", "raw_message", "sub_type" })
        {
            if (item.TryGetProperty(name, out var value))
            {
                payload[name] = value.Clone();
            }
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string GetIdText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? "",
            _ => ""
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ingest.StatusChanged -= OnStatusChanged;
        _gate.Dispose();
        _cursor.Dispose();
    }
}

/// <summary>历史消息规范化结果（游标稳定键 + 注入用事件 JSON）。</summary>
/// <param name="MessageId">消息 id（稳定键优先）。</param>
/// <param name="TimestampUnix">原始时间戳（Unix 秒）。</param>
/// <param name="ReceivedAt">原始时间（入档时间）。</param>
/// <param name="MappedJson">规范化后的官方事件 JSON。</param>
/// <param name="ContentHash">时间 + 内容哈希（兜底去重键）。</param>
public sealed record NapCatHistoryMessage(
    string MessageId, long TimestampUnix, DateTimeOffset ReceivedAt, string MappedJson, string ContentHash);

/// <summary>一次启动核对的结果统计。</summary>
public sealed record NapCatBackfillReport(int Groups, int Injected, int Skipped, DateTimeOffset RanAt)
{
    public static readonly NapCatBackfillReport Empty = new(0, 0, 0, DateTimeOffset.MinValue);
}
