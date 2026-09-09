using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>NapCat API 调用委托：action + 参数 → 成功回包的 data（失败/超时为 null）。</summary>
public delegate Task<JsonElement?> NapCatApiInvoker(
    string action, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct);

/// <summary>
/// NapCat 群文件直链解析器：把规范化事件 JSON 中 file 段附件的下载直链补全/刷新。
/// <para>
/// 背景：NapCat 上报的普通文件直链有下载次数限制，且部分 file 段不带 url。
/// 本类经 NapCat API <c>get_group_file_url</c>（参数 file_id + group_id）获取新鲜直链，
/// 就地写回附件 <c>url</c> 字段，使消息无需改动即可流入既有文件管道（下载 → 学科归档）。
/// </para>
/// <para>
/// 保守纪律：仅处理群消息（私聊占位群跳过）、仅处理携带 file_id 的 file 段；
/// API 失败/回包缺 url 时保留事件自带的 url（可能为空 → 文件管道按「缺少下载地址」重试语义处理）。
/// 任何异常都向上抛由调用方兜底（按原 JSON 分发），绝不吞掉取消信号。
/// </para>
/// </summary>
public static class NapCatFileUrlResolver
{
    /// <summary>对规范化事件 JSON 做 file 段直链补全。无 file 段/私聊/解析失败时原样返回。</summary>
    public static async Task<string> EnrichAsync(
        string mappedJson, NapCatApiInvoker api, ILogger? logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappedJson);
        ArgumentNullException.ThrowIfNull(api);

        var node = JsonNode.Parse(mappedJson);
        if (node is not JsonObject root)
        {
            return mappedJson;
        }

        var group = TryGetString(root, "group_openid", out var g) ? g : "";
        if (group.Length == 0 || group.StartsWith(NapCatEventNormalizer.PrivateGroupPrefix, StringComparison.Ordinal))
        {
            return mappedJson; // 私聊消息不走群文件 API
        }

        if (root["attachments"] is not JsonArray attachments)
        {
            return mappedJson;
        }

        var changed = false;
        foreach (var item in attachments)
        {
            if (item is not JsonObject attachment
                || !TryGetString(attachment, "content_type", out var contentType)
                || contentType != "file"
                || !TryGetString(attachment, "file_id", out var fileId))
            {
                continue;
            }

            var url = await FetchUrlAsync(api, fileId, group, logger, ct).ConfigureAwait(false);
            if (url.Length == 0)
            {
                continue; // 保留事件自带 url（可能为空 → 文件管道缺失地址语义，可重试）
            }

            attachment["url"] = url;
            changed = true;
        }

        return changed ? root.ToJsonString() : mappedJson;
    }

    /// <summary>读取对象字符串属性（缺失/非字符串/空串均返回 false）。</summary>
    private static bool TryGetString(JsonObject obj, string name, out string value)
    {
        value = obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";
        return value.Length > 0;
    }

    /// <summary>调用 get_group_file_url 获取直链（失败记日志返回空串，不抛非取消异常）。</summary>
    private static async Task<string> FetchUrlAsync(
        NapCatApiInvoker api, string fileId, string group, ILogger? logger, CancellationToken ct)
    {
        JsonElement? response;
        try
        {
            // group_id 与 group 双写：兼容 NapCat 文档两种参数名写法（多余参数被忽略）
            response = await api("get_group_file_url", new Dictionary<string, object?>
            {
                ["file_id"] = fileId,
                ["group_id"] = group,
                ["group"] = group
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "NapCat 群文件直链获取失败（file_id={FileId}），保留事件自带 URL", fileId);
            return "";
        }

        if (response is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String)
        {
            logger?.LogInformation("NapCat 群文件直链回包不含 url（file_id={FileId}），保留事件自带 URL", fileId);
            return "";
        }

        var url = urlEl.GetString() ?? "";
        logger?.LogInformation("NapCat 群文件直链已解析（file_id={FileId}）", fileId);
        return url;
    }
}
