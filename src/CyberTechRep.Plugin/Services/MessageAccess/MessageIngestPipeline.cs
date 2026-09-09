using System.Text.Json;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>
/// 消息接入管道：官方平台分发事件 → MessageRecord 的映射、群白名单过滤、幂等去重、脱敏快照。
/// 纯管道逻辑（无传输依赖），独立可测。
/// </summary>
public sealed class MessageIngestPipeline
{
    private readonly MessageIdempotencyStore _idempotencyStore;
    private readonly ILogger? _logger;
    private readonly object _settingsLock = new();
    private IReadOnlyList<string> _groupWhitelist = [];
    private bool _whitelistEmptyWarned;

    public MessageIngestPipeline(MessageIdempotencyStore idempotencyStore, ILogger? logger = null)
    {
        _idempotencyStore = idempotencyStore;
        _logger = logger;
    }

    /// <summary>管道输出：通过白名单与幂等检查的新消息。</summary>
    public event EventHandler<MessageRecord>? MessageReceived;

    /// <summary>管道输出：通过群白名单过滤的撤回事件。</summary>
    public event EventHandler<MessageRecallEvent>? MessageRecalled;

    /// <summary>设置热更新（设置页修改群白名单后即时生效）。</summary>
    public void UpdateSettings(ConnectionSettings settings)
    {
        lock (_settingsLock)
        {
            _groupWhitelist = settings.GroupWhitelist ?? [];
            _whitelistEmptyWarned = false;
        }

        _logger?.LogInformation("接入管道配置已更新：群白名单 {Count} 个", _groupWhitelist.Count);
    }

    /// <summary>
    /// 处理一条分发事件。返回处理结果（供日志/测试断言），事件通过 <see cref="MessageReceived"/> 输出。
    /// </summary>
    /// <param name="eventType">官方事件类型。</param>
    /// <param name="dataJson">事件 JSON。</param>
    /// <param name="receivedAt">消息时间覆盖（断点续传补齐历史消息时传原始时间；null = 当前时间）。</param>
    public PipelineResult HandleDispatch(string eventType, string dataJson, DateTimeOffset? receivedAt = null)
    {
        if (eventType is not (GroupEventTypes.GroupMessageCreate or GroupEventTypes.GroupAtMessageCreate))
        {
            return PipelineResult.Ignored($"非群消息事件：{eventType}");
        }

        GroupMessageEvent ev;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            return PipelineResult.Dropped("事件 JSON 解析失败", ex.Message);
        }

        if (string.IsNullOrEmpty(ev.Id))
        {
            return PipelineResult.Dropped("事件缺少消息 id（幂等键）", dataJson);
        }

        // 幂等：协议端重发/重复推送只处理一次
        if (!_idempotencyStore.TryMarkSeen(ev.Id))
        {
            return PipelineResult.Ignored($"重复消息：id={ev.Id}");
        }

        // 群白名单：非空时只接收白名单群；空时接收全部并告警一次
        lock (_settingsLock)
        {
            if (_groupWhitelist.Count > 0 && !_groupWhitelist.Contains(ev.GroupOpenId))
            {
                _logger?.LogDebug("消息被群白名单过滤：group={Group} id={Id}", ev.GroupOpenId, ev.Id);
                return PipelineResult.Ignored($"群不在白名单：{ev.GroupOpenId}");
            }

            if (_groupWhitelist.Count == 0 && !_whitelistEmptyWarned)
            {
                _whitelistEmptyWarned = true;
                _logger?.LogWarning("群白名单为空：当前接收全部群消息，请在设置页配置白名单");
            }
        }

        var record = MapToRecord(ev, dataJson, receivedAt);
        _logger?.LogInformation(
            "群消息：id={Id} group={Group} sender={Sender} text={Text} attachments={Attachments}",
            record.MessageId, record.GroupOpenId, record.MemberOpenId,
            Summarize(record), record.Segments.Count(s => s.Type != SegmentTypes.Text));

        MessageReceived?.Invoke(this, record);
        return PipelineResult.Accepted();
    }

    /// <summary>
    /// 处理一条撤回事件：群白名单过滤（与消息同一口径）。
    /// 撤回不做幂等去重——同一消息重复上报撤回是幂等删除操作，重复删除无副作用。
    /// </summary>
    public PipelineResult HandleRecall(MessageRecallEvent recall)
    {
        ArgumentNullException.ThrowIfNull(recall);
        if (string.IsNullOrEmpty(recall.MessageId))
        {
            return PipelineResult.Dropped("撤回事件缺少 message_id", null);
        }

        lock (_settingsLock)
        {
            if (_groupWhitelist.Count > 0
                && recall.GroupOpenId.Length > 0
                && !_groupWhitelist.Contains(recall.GroupOpenId))
            {
                _logger?.LogDebug("撤回事件被群白名单过滤：group={Group} id={Id}",
                    recall.GroupOpenId, recall.MessageId);
                return PipelineResult.Ignored($"群不在白名单：{recall.GroupOpenId}");
            }
        }

        _logger?.LogInformation("撤回事件：id={Id} group={Group} operator={Operator}",
            recall.MessageId, recall.GroupOpenId, recall.OperatorOpenId);
        MessageRecalled?.Invoke(this, recall);
        return PipelineResult.Accepted();
    }

    /// <summary>事件体 → MessageRecord（含脱敏原始快照）。</summary>
    /// <param name="ev">事件体。</param>
    /// <param name="rawJson">原始 JSON（脱敏快照）。</param>
    /// <param name="receivedAt">
    /// 时间覆盖（断点续传补齐历史消息时传原始时间）。为 null 时优先采用协议端原始时间
    /// <see cref="GroupMessageEvent.RawTimestamp"/>（NapCat 透传 OneBot <c>time</c>），
    /// 缺失/非法才回落到本地当前时间——保证「实时」与「历史补齐」两条路径的时间基准同源。
    /// </param>
    public static MessageRecord MapToRecord(GroupMessageEvent ev, string rawJson,
        DateTimeOffset? receivedAt = null)
    {
        var segments = new List<MessageSegment>();
        if (!string.IsNullOrEmpty(ev.Content))
        {
            segments.Add(new MessageSegment { Type = SegmentTypes.Text, Text = ev.Content });
        }

        foreach (var a in ev.Attachments)
        {
            var type = a.ContentType switch
            {
                var ct when ct.StartsWith("image/") => SegmentTypes.Image,
                var ct when ct.StartsWith("video/") => SegmentTypes.Video,
                "voice" => SegmentTypes.Voice,
                "file" => SegmentTypes.File,
                _ => SegmentTypes.File
            };
            segments.Add(new MessageSegment
            {
                Type = type,
                Url = a.Url,
                FileName = a.FileName,
                FileSize = a.Size
            });
        }

        var sourceTimestamp = ParseSourceTimestamp(ev.RawTimestamp);
        var resolvedReceivedAt = receivedAt
            ?? (sourceTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(sourceTimestamp).ToLocalTime()
                : DateTimeOffset.Now);

        return new MessageRecord
        {
            MessageId = ev.Id,
            GroupOpenId = ev.GroupOpenId,
            MemberOpenId = ev.MemberOpenId,
            SenderNickname = ev.SenderNickname,
            ReceivedAt = resolvedReceivedAt,
            SourceTimestampUnix = sourceTimestamp,
            Segments = segments,
            ReplyToMessageId = ev.ReplyToMessageId,
            RawJsonSnapshot = SanitizeRawSnapshot(rawJson)
        };
    }

    /// <summary>协议端原始时间戳解析（Unix 秒）：缺失/非法/明显异常（2000 年前或未来 1 天以上）→ 0。</summary>
    internal static long ParseSourceTimestamp(string? rawTimestamp)
    {
        if (!long.TryParse(rawTimestamp, out var seconds) || seconds <= 0)
        {
            return 0;
        }

        const long Year2000 = 946684800;
        var upperBound = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        return seconds is >= Year2000 and var s && s <= upperBound ? seconds : 0;
    }

    /// <summary>
    /// 原始事件快照脱敏：本层不写入任何凭据字段（凭据只在 HTTP 头/表单），此处做长度兜底。
    /// <para>
    /// internal：<see cref="NapCatBackfillService"/> 计算续传游标的稳定键时必须用同一函数处理
    /// 事件 JSON，否则实时路径（记录 <see cref="MessageRecord.RawJsonSnapshot"/>）与核对路径
    /// 的哈希输入不一致，同秒兜底去重会失效。
    /// </para>
    /// </summary>
    internal static string SanitizeRawSnapshot(string rawJson)
    {
        return rawJson.Length <= 64 * 1024 ? rawJson : rawJson[..(64 * 1024)] + "…(截断)";
    }

    private static string Summarize(MessageRecord record)
    {
        var text = record.Segments.FirstOrDefault(s => s.Type == SegmentTypes.Text)?.Text ?? "";
        return text.Length <= 50 ? text : text[..50] + "…";
    }
}

/// <summary>管道处理结果（用于验收日志与单元测试断言）。</summary>
public enum PipelineAction { Accepted, Ignored, Dropped }

public sealed record PipelineResult(PipelineAction Action, string Reason, string? Error = null)
{
    public static PipelineResult Accepted() => new(PipelineAction.Accepted, "");
    public static PipelineResult Ignored(string reason) => new(PipelineAction.Ignored, reason);
    public static PipelineResult Dropped(string reason, string? error = null) => new(PipelineAction.Dropped, reason, error);
}
