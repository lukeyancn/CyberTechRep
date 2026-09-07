using System.Text.Json;

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
/// image 段转 attachments（复用官方附件下载链路）；字符串 message 整体作为纯文本 content。
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
                return NapCatEventResult.Ignored($"不支持分发的事件类型：{postType}");
            }

            if (postType != "message")
            {
                return NapCatEventResult.Ignored($"未知 post_type：{postType}");
            }

            return MapMessageEvent(root);
        }
    }

    private static NapCatEventResult MapMessageEvent(JsonElement root)
    {
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

        // message 段：数组格式（text/face/at/image/...）或纯字符串
        var content = new List<string>();
        var attachments = new List<object>();
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

                        default:
                            // record/ video/ file/ json 等：文本与附件链路暂不消费，保守忽略不误映射
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
            attachments
        };

        return new NapCatEventResult
        {
            Handled = true,
            DispatchType = GroupEventTypes.GroupMessageCreate,
            MappedJson = JsonSerializer.Serialize(mapped)
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
