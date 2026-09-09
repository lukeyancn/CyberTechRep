using System.Text.Json;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>OneBot 11 事件规范化结果。</summary>
public sealed record NapCatEventResult
{
    /// <summary>true = 已规范化为官方事件 JSON，需向管道分发。</summary>
    public bool Handled { get; init; }

    /// <summary>规范化后的官方事件类型（固定 GROUP_MESSAGE_CREATE）。</summary>
    public string DispatchType { get; init; } = "";

    /// <summary>官方事件格式 JSON（GroupMessageEvent.Parse 可解析）。</summary>
    public string MappedJson { get; init; } = "";

    /// <summary>忽略原因（心跳/回包/不支持事件等；仅 !Handled 时有意义）。</summary>
    public string IgnoreReason { get; init; } = "";

    /// <summary>true = 消息携带 file 段附件，分发前需经 <see cref="NapCatFileUrlResolver"/> 补全/刷新下载直链。</summary>
    public bool HasFileAttachment { get; init; }

    /// <summary>
    /// 撤回事件（<c>notice.group_recall</c> / <c>notice.friend_recall</c>）：非空时经
    /// <see cref="NapCatWsClient"/> 的 MessageRecalled 通道分发，不进入消息管道。
    /// </summary>
    public MessageRecallEvent? Recall { get; init; }

    public static NapCatEventResult Ignored(string reason) => new() { Handled = false, IgnoreReason = reason };
}

/// <summary>
/// OneBot 11 事件 → 官方平台群消息事件（GROUP_MESSAGE_CREATE 事件体格式）的纯规范化映射。
/// <para>
/// 目标：让 <see cref="MessageIngestPipeline"/>（映射/白名单/幂等）零改动复用——
/// NapCat 客户端收到事件后先经本类转换为官方事件 JSON，再走既有 DispatchReceived 通道。
/// </para>
/// <para>
/// 字段映射：message_id→id；group_id→group_openid（私聊用 "private:{user_id}" 占位，
/// 群白名单可按需放行）；user_id→author.member_openid；sender.nickname→author.username；
/// message 数组段拼接为 content（text 原文 / at → [@qq] / face → [表情]），
/// image/file 段转 attachments（image 复用官方附件下载链路；file 携带 file_id，分发前
/// 经 <see cref="NapCatFileUrlResolver"/> 刷新直链后走同一附件链路）；字符串 message 整体作为纯文本 content。
/// meta_event（心跳/生命周期）、echo API 回包、notice/request 事件一律忽略不下发。
/// </para>
/// </summary>
public static class NapCatEventNormalizer
{
    /// <summary>私聊消息在群 OpenID 字段的占位前缀（白名单按此字符串匹配可选择性放行私聊）。</summary>
    public const string PrivateGroupPrefix = "private:";

    public static NapCatEventResult Normalize(string rawJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return NapCatEventResult.Ignored("事件不是合法 JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NapCatEventResult.Ignored("事件根不是 JSON 对象");
            }

            // echo = API 调用回包（NapCat 正向 WS 会回显 get_status 等调用结果）
            var postType = GetStr(root, "post_type");
            if (postType.Length == 0)
            {
                if (root.TryGetProperty("echo", out _) || root.TryGetProperty("retcode", out _))
                {
                    return NapCatEventResult.Ignored("API 调用回包（echo）");
                }

                return NapCatEventResult.Ignored("缺少 post_type 字段");
            }

            if (postType == "meta_event")
            {
                return NapCatEventResult.Ignored($"元事件：{GetStr(root, "meta_event_type")}");
            }

            if (postType is "notice" or "request" or "message_sent")
            {
                // 撤回上报（OneBot 11）：notice.group_recall / notice.friend_recall → 撤回事件。
                // 其余 notice（group_increase 等）/request 维持现状忽略。
                if (postType == "notice")
                {
                    var recall = TryMapRecall(root);
                    if (recall is not null)
                    {
                        return new NapCatEventResult
                        {
                            Handled = true,
                            DispatchType = "MESSAGE_RECALL",
                            MappedJson = JsonSerializer.Serialize(recall),
                            Recall = recall
                        };
                    }
                }

                return NapCatEventResult.Ignored($"不支持分发的事件类型：{postType}");
            }

            if (postType != "message")
            {
                return NapCatEventResult.Ignored($"未知 post_type：{postType}");
            }

            return MapMessageEvent(root);
        }
    }

    /// <summary>
    /// 撤回事件映射：<c>notice_type</c> = <c>group_recall</c> / <c>friend_recall</c> →
    /// <see cref="MessageRecallEvent"/>；其余 notice 类型返回 null（维持忽略）。
    /// 缺少 message_id 时返回 null（无法定位被撤回的消息）。
    /// </summary>
    public static MessageRecallEvent? TryMapRecall(JsonElement root)
    {
        var noticeType = GetStr(root, "notice_type");
        if (noticeType is not ("group_recall" or "friend_recall"))
        {
            return null;
        }

        var messageId = GetNumberAsString(root, "message_id");
        if (messageId.Length == 0)
        {
            return null;
        }

        var userId = GetNumberAsString(root, "user_id");
        var groupKey = noticeType == "group_recall"
            ? GetNumberAsString(root, "group_id")
            : PrivateGroupPrefix + userId;

        var recalledAt = DateTimeOffset.Now;
        if (root.TryGetProperty("time", out var timeEl)
            && timeEl.ValueKind == JsonValueKind.Number
            && timeEl.TryGetInt64(out var seconds)
            && seconds > 0)
        {
            try
            {
                recalledAt = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                // 时间戳异常：按到达时间处理
            }
        }

        return new MessageRecallEvent
        {
            MessageId = messageId,
            GroupOpenId = groupKey,
            SenderOpenId = userId,
            OperatorOpenId = GetNumberAsString(root, "operator_id"),
            RecalledAt = recalledAt,
            FromProtocolNotice = true
        };
    }

    /// <summary>
    /// 原始帧是否为 OneBot 生命周期/心跳元事件（<c>meta_event.lifecycle</c> / <c>meta_event.heartbeat</c>）。
    /// NapCat 连接状态机据此从「传输层就绪」升级为「已连接」——握手成功本身不足以判定可用。
    /// </summary>
    public static bool IsLifecycleOrHeartbeat(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || GetStr(root, "post_type") != "meta_event")
            {
                return false;
            }

            return GetStr(root, "meta_event_type") is "lifecycle" or "heartbeat";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static NapCatEventResult MapMessageEvent(JsonElement root)    {
        var messageType = GetStr(root, "message_type");
        if (messageType is not ("group" or "private"))
        {
            return NapCatEventResult.Ignored($"未知 message_type：{messageType}");
        }

        var messageId = GetNumberAsString(root, "message_id");
        if (messageId.Length == 0)
        {
            return NapCatEventResult.Ignored("缺少 message_id（幂等键）");
        }

        var userId = GetNumberAsString(root, "user_id");
        var groupKey = messageType == "group"
            ? GetNumberAsString(root, "group_id")
            : PrivateGroupPrefix + userId;

        var nickname = "";
        if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            nickname = GetStr(sender, "nickname");
            if (nickname.Length == 0)
            {
                nickname = GetStr(sender, "card");
            }
        }

        // message 段：数组格式（text/face/at/image/file/...）或纯字符串
        var content = new List<string>();
        var attachments = new List<object>();
        var hasFileAttachment = false;
        var replyTo = "";
        if (root.TryGetProperty("message", out var messageEl))
        {
            if (messageEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in messageEl.EnumerateArray())
                {
                    if (seg.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var segType = GetStr(seg, "type");
                    var data = seg.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                    switch (segType)
                    {
                        case "text":
                            var text = data.ValueKind == JsonValueKind.Object ? GetStr(data, "text") : "";
                            if (text.Length > 0)
                            {
                                content.Add(text);
                            }

                            break;

                        case "at":
                            var qq = data.ValueKind == JsonValueKind.Object
                                ? GetStr(data, "qq") is { Length: > 0 } q ? q : GetNumberAsString(data, "qq")
                                : "";
                            content.Add($"[@{qq}]");
                            break;

                        case "face":
                            content.Add("[表情]");
                            break;

                        case "reply":
                            // 需求 1：回复段（引用某条消息）→ 透传被回复消息 id，
                            // 由消息管道判定「被回复的是作业/通知」并按对方类型与学科归并。
                            if (data.ValueKind == JsonValueKind.Object)
                            {
                                replyTo = GetStr(data, "id") is { Length: > 0 } rid
                                    ? rid
                                    : GetNumberAsString(data, "id");
                            }

                            break;

                        case "image":
                            if (data.ValueKind == JsonValueKind.Object)
                            {
                                var file = GetStr(data, "file");
                                var url = GetStr(data, "url");
                                attachments.Add(new
                                {
                                    content_type = ImageContentType(url, file),
                                    filename = file,
                                    url
                                });
                            }

                            break;

                        case "file":
                            // 群文件段 → 附件（content_type="file" 走既有文件管道归档链路）。
                            // 直链可能缺失或受下载次数限制：file_id 随事件透传，分发前由
                            // NapCatWsClient 经 NapCatFileUrlResolver 调 get_group_file_url 刷新。
                            if (data.ValueKind == JsonValueKind.Object)
                            {
                                var fileId = GetStr(data, "file_id") is { Length: > 0 } fid
                                    ? fid
                                    : GetNumberAsString(data, "file_id");
                                var fileName = GetStr(data, "file");
                                if (fileName.Length == 0)
                                {
                                    fileName = fileId;
                                }

                                attachments.Add(new
                                {
                                    content_type = "file",
                                    filename = fileName,
                                    url = GetStr(data, "url"),
                                    size = GetFileLength(data),
                                    file_id = fileId
                                });
                                hasFileAttachment = true;
                            }

                            break;

                        default:
                            // record/ video/ json 等：文本与附件链路暂不消费，保守忽略不误映射
                            break;
                    }
                }
            }
            else if (messageEl.ValueKind == JsonValueKind.String)
            {
                // 字符串格式（可能是 CQ 码）：整体作为纯文本（保守处理，不做 CQ 码解析）
                var raw = messageEl.GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    content.Add(raw);
                }
            }
        }

        var mapped = new
        {
            id = messageId,
            group_openid = groupKey,
            author = new { member_openid = userId, username = nickname },
            content = string.Join("", content),
            reply_to = replyTo,
            // 需求 4：透传 OneBot 原始时间（Unix 秒）——续传游标与启动核对共用同一时间基准
            timestamp = GetNumberAsString(root, "time"),
            attachments
        };

        return new NapCatEventResult
        {
            Handled = true,
            DispatchType = GroupEventTypes.GroupMessageCreate,
            MappedJson = JsonSerializer.Serialize(mapped),
            HasFileAttachment = hasFileAttachment
        };
    }

    /// <summary>file 段 file_size（OneBot 数字/字符串）→ 字节数。</summary>
    private static long? GetFileLength(JsonElement data)
    {
        if (!data.TryGetProperty("file_size", out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.String when long.TryParse(v.GetString(), out var l) => l,
            _ => null
        };
    }

    /// <summary>image 段 → 官方附件 content_type（MapToRecord 按 "image/" 前缀识别图片）。</summary>
    private static string ImageContentType(string url, string fileName)
    {
        var probe = url.Length > 0 ? url : fileName;
        foreach (var (ext, ct) in new[] { (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"),
                     (".gif", "image/gif"), (".webp", "image/webp"), (".bmp", "image/bmp") })
        {
            if (probe.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return ct;
            }
        }

        return "image/octet-stream";
    }

    private static string GetStr(JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
    }

    /// <summary>数值字段（user_id/group_id/time 等 OneBot 数字 ID）→ 字符串（容错：字符串直接取）。</summary>
    private static string GetNumberAsString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return "";
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString() : v.GetRawText(),
            JsonValueKind.String => v.GetString() ?? "",
            _ => ""
        };
    }
}
