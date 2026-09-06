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
    public PipelineResult HandleDispatch(string eventType, string dataJson)
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

        var record = MapToRecord(ev, dataJson);
        _logger?.LogInformation(
            "群消息：id={Id} group={Group} sender={Sender} text={Text} attachments={Attachments}",
            record.MessageId, record.GroupOpenId, record.MemberOpenId,
            Summarize(record), record.Segments.Count(s => s.Type != SegmentTypes.Text));

        MessageReceived?.Invoke(this, record);
        return PipelineResult.Accepted();
    }

    /// <summary>事件体 → MessageRecord（含脱敏原始快照）。</summary>
    public static MessageRecord MapToRecord(GroupMessageEvent ev, string rawJson)
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

        return new MessageRecord
        {
            MessageId = ev.Id,
            GroupOpenId = ev.GroupOpenId,
            MemberOpenId = ev.MemberOpenId,
            SenderNickname = ev.SenderNickname,
            ReceivedAt = DateTimeOffset.Now,
            Segments = segments,
            RawJsonSnapshot = Sanitize(rawJson)
        };
    }

    /// <summary>原始事件快照脱敏：本层不写入任何凭据字段（凭据只在 HTTP 头/表单），此处做长度兜底。</summary>
    private static string Sanitize(string rawJson)
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
