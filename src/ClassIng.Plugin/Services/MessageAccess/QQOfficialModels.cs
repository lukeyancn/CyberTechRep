using System.Text.Json;

namespace ClassIng.Plugin.Services.MessageAccess;

/// <summary>
/// QQ 官方机器人开放平台 WebSocket 网关的帧模型（op/t/d/s）。
/// 协议依据：bot.qq.com/wiki 开发文档 v2（2026-07 版）。
/// </summary>
public static class GatewayOp
{
    /// <summary>服务端推送：分发事件。</summary>
    public const int Dispatch = 0;

    /// <summary>客户端发送：心跳。</summary>
    public const int Heartbeat = 1;

    /// <summary>客户端发送：鉴权（识别）。</summary>
    public const int Identify = 2;

    /// <summary>客户端发送：恢复会话。</summary>
    public const int Resume = 6;

    /// <summary>客户端发送：重连请求（服务端要求）。</summary>
    public const int Reconnect = 7;

    /// <summary>服务端推送：请求客户端重连。</summary>
    public const int ServerReconnect = 7;

    /// <summary>服务端推送：会话失效（需重新 Identify）。</summary>
    public const int InvalidSession = 9;

    /// <summary>服务端推送：Hello（含心跳间隔）。</summary>
    public const int Hello = 10;

    /// <summary>服务端推送：心跳确认。</summary>
    public const int HeartbeatAck = 11;
}

/// <summary>本项目关注的群消息分发事件类型。</summary>
public static class GroupEventTypes
{
    /// <summary>群消息（全量模式，需群主开启「接收所有消息」）。</summary>
    public const string GroupMessageCreate = "GROUP_MESSAGE_CREATE";

    /// <summary>群 @机器人 消息（兜底模式）。</summary>
    public const string GroupAtMessageCreate = "GROUP_AT_MESSAGE_CREATE";
}

/// <summary>GROUP_MESSAGE_CREATE / GROUP_AT_MESSAGE_CREATE 事件体（容错解析结果）。</summary>
public sealed class GroupMessageEvent
{
    /// <summary>消息 id（幂等键）。</summary>
    public string Id { get; init; } = "";

    public string GroupOpenId { get; init; } = "";

    public string MemberOpenId { get; init; } = "";

    /// <summary>发送者昵称（事件未携带时为空）。</summary>
    public string SenderNickname { get; init; } = "";

    /// <summary>纯文本内容。</summary>
    public string Content { get; init; } = "";

    /// <summary>附件列表（图片/文件/视频/语音，带下载直链）。</summary>
    public IReadOnlyList<GroupAttachment> Attachments { get; init; } = [];

    public string RawTimestamp { get; init; } = "";

    /// <summary>从事件 JSON（d 字段）容错解析。</summary>
    public static GroupMessageEvent Parse(JsonElement d)
    {
        string GetStr(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }

        var attachments = new List<GroupAttachment>();
        if (d.TryGetProperty("attachments", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                attachments.Add(new GroupAttachment
                {
                    ContentType = GetStr(a, "content_type"),
                    FileName = GetStr(a, "filename") is { Length: > 0 } f ? f : GetStr(a, "file_name"),
                    Url = GetStr(a, "url"),
                    Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : null
                });
            }
        }

        string memberOpenId = "", nickname = "";
        if (d.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
        {
            memberOpenId = GetStr(author, "member_openid");
            nickname = GetStr(author, "username") is { Length: > 0 } u ? u : GetStr(author, "nickname");
        }

        return new GroupMessageEvent
        {
            Id = GetStr(d, "id"),
            GroupOpenId = GetStr(d, "group_openid"),
            MemberOpenId = memberOpenId,
            SenderNickname = nickname,
            Content = GetStr(d, "content"),
            Attachments = attachments,
            RawTimestamp = GetStr(d, "timestamp")
        };
    }
}

/// <summary>群消息附件。</summary>
public sealed class GroupAttachment
{
    /// <summary>image/jpeg、image/png、video/mp4、voice、file 等。</summary>
    public string ContentType { get; init; } = "";

    public string FileName { get; init; } = "";

    /// <summary>下载直链（含签名 rkey，有时效）。</summary>
    public string Url { get; init; } = "";

    public long? Size { get; init; }
}
