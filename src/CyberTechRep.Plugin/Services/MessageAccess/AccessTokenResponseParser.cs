using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>AccessToken 响应解析结果。</summary>
/// <param name="Success">是否成功取到 token。</param>
/// <param name="Token">token 明文（成功时非空；绝不写入日志）。</param>
/// <param name="ExpiresIn">有效期秒数（缺省 7200）。</param>
/// <param name="Diagnostic">失败诊断文本（含脱敏后的原始响应片段，供日志与 UI 展示）。</param>
public sealed record AccessTokenParseResult(bool Success, string Token, int ExpiresIn, string Diagnostic)
{
    public static AccessTokenParseResult Ok(string token, int expiresIn) => new(true, token, expiresIn, "");
}

/// <summary>
/// AccessToken 响应兼容解析器（纯函数、可单测）。
/// <para>
/// <b>缺陷根因</b>：原实现只认顶层 <c>access_token</c> 字段且要求 HTTP 200，
/// 一旦协议端/代理/自建网关返回的响应结构略有差异（字段嵌套在 <c>data</c> 下、
/// 驼峰 <c>accessToken</c>、token 字段名不同、响应体是「JSON 字符串里再套 JSON」、
/// 或失败信息随 HTTP 200 返回 <c>{"code":100007,"message":"..."}</c>），
/// 就会抛出「AccessToken 响应缺少 access_token 字段」——把「拿不到 token 的真实原因」
/// 全部吞掉，用户只能看到一句无信息量的报错。
/// </para>
/// <para>
/// <b>修复口径</b>：先按「官方标准结构」解析，再逐级兼容常见变体；任何一步失败都把
/// <b>脱敏后的原始响应片段</b>带进诊断文本（日志 + 抛出的异常消息），
/// 让「字段缺失」变成可自助排查的具体线索。
/// </para>
/// <para>
/// 脱敏纪律：token / secret / key 等字段值一律替换为 <c>***</c>，
/// 长十六进制/Base64 串按长度打码，避免凭据经日志外泄。
/// </para>
/// </summary>
public static class AccessTokenResponseParser
{
    /// <summary>诊断片段最大长度（防日志膨胀）。</summary>
    public const int SnippetMaxLength = 512;

    /// <summary>候选 token 字段名（大小写不敏感；按优先级顺序匹配）。</summary>
    private static readonly string[] TokenFieldNames =
        ["access_token", "accesstoken", "access_token_value", "token", "app_access_token"];

    /// <summary>候选 expires_in 字段名（大小写不敏感）。</summary>
    private static readonly string[] ExpiresFieldNames =
        ["expires_in", "expiresin", "expires", "expire_in", "valid_seconds"];

    /// <summary>可能的嵌套容器字段（大小写不敏感）。</summary>
    private static readonly string[] ContainerFieldNames = ["data", "result", "response", "body", "payload"];

    /// <summary>敏感字段名（值必须脱敏）。</summary>
    private static readonly Regex SensitiveFieldRegex = new(
        "\"(?:access_?token|accessToken|token|client_?secret|clientSecret|app_?secret|appSecret|secret|password|api_?key|apiKey|authorization)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>裸的长十六进制/Base64 串（无字段名上下文时的凭据兜底打码）。</summary>
    private static readonly Regex LongTokenLikeRegex = new(
        "[A-Za-z0-9_\\-\\.]{24,}",
        RegexOptions.Compiled);

    /// <summary>解析 token 响应体。失败时 <see cref="AccessTokenParseResult.Diagnostic"/> 含脱敏原始片段。</summary>
    public static AccessTokenParseResult Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AccessTokenParseResult(false, "", 7200, "响应体为空（协议端未返回任何内容）");
        }

        // ① 直接解析
        if (TryParseObject(body, out var result, out var error))
        {
            return result!;
        }

        // ② 双编码兼容：响应体本身是 JSON 字符串，其内容是真正的 JSON（部分自建网关会这样包一层）
        if (TryUnwrapJsonString(body, out var inner) && TryParseObject(inner, out result, out _))
        {
            return result!;
        }

        return new AccessTokenParseResult(false, "", 7200, error!);
    }

    /// <summary>从单个 JSON 对象中提取 token（含嵌套容器与驼峰变体）。</summary>
    private static bool TryParseObject(string body, out AccessTokenParseResult? result, out string? error)
    {
        result = null;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            error = $"响应不是合法 JSON（{ex.Message}）；原始响应片段：{SanitizeSnippet(body)}";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"响应 JSON 根节点是 {root.ValueKind} 而非对象；原始响应片段：{SanitizeSnippet(body)}";
                return false;
            }

            // 顶层优先
            if (TryExtractToken(root, out var token))
            {
                result = AccessTokenParseResult.Ok(token, ExtractExpiresIn(root));
                return true;
            }

            // 嵌套容器（data/result/response/...）
            foreach (var containerName in ContainerFieldNames)
            {
                if (!TryGetPropertyIgnoreCase(root, containerName, out var container))
                {
                    continue;
                }

                if (container.ValueKind == JsonValueKind.String)
                {
                    // 容器本身是字符串：可能是「JSON 字符串里再套 JSON」
                    var text = container.GetString() ?? "";
                    if (TryUnwrapJsonString(text, out var nested)
                        && TryParseObject(nested, out result, out _))
                    {
                        return true;
                    }

                    continue;
                }

                if (container.ValueKind == JsonValueKind.Object && TryExtractToken(container, out token))
                {
                    result = AccessTokenParseResult.Ok(token, ExtractExpiresIn(container, root));
                    return true;
                }
            }

            // 失败：把协议端自带的错误信息一并带出（官方失败响应形如 {"code":100007,"message":"..."}）
            var reason = ExtractErrorMessage(root);
            error = string.IsNullOrEmpty(reason)
                ? $"响应中未找到 access_token 字段（已尝试顶层与 data/result 嵌套、驼峰变体）；原始响应片段：{SanitizeSnippet(body)}"
                : $"响应中未找到 access_token 字段；协议端错误：{reason}；原始响应片段：{SanitizeSnippet(body)}";
            return false;
        }
    }

    /// <summary>提取 token（候选字段名 + 字符串/数字值 + 值内再套 JSON 的极端变体）。</summary>
    private static bool TryExtractToken(JsonElement obj, out string token)
    {
        token = "";
        foreach (var name in TokenFieldNames)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var value))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            };
            if (text.Length == 0)
            {
                continue;
            }

            // 值本身是 JSON 对象文本（如 "{\"access_token\":\"...\"}"）→ 再解一层
            if (text.Length > 2 && (text[0] == '{' || text[0] == '"')
                && TryUnwrapJsonString(text, out var inner)
                && TryParseObject(inner, out var nested, out _)
                && nested is { Success: true })
            {
                token = nested.Token;
                return true;
            }

            token = text;
            return true;
        }

        return false;
    }

    /// <summary>提取有效期（自身优先，其次容器外层）。</summary>
    private static int ExtractExpiresIn(params JsonElement[] scopes)
    {
        foreach (var scope in scopes)
        {
            if (scope.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var name in ExpiresFieldNames)
            {
                if (!TryGetPropertyIgnoreCase(scope, name, out var value))
                {
                    continue;
                }

                var parsed = value.ValueKind switch
                {
                    JsonValueKind.Number when value.TryGetInt32(out var n) => n,
                    JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
                    _ => 0
                };
                if (parsed > 0)
                {
                    return parsed;
                }
            }
        }

        return 7200;
    }

    /// <summary>提取协议端错误说明（code/message/msg/error/error_description）。</summary>
    private static string ExtractErrorMessage(JsonElement root)
    {
        var parts = new List<string>();
        foreach (var name in new[] { "code", "errcode", "retcode", "message", "msg", "error", "error_description" })
        {
            if (TryGetPropertyIgnoreCase(root, name, out var value) && value.ValueKind is not JsonValueKind.Null)
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
                if (text.Length > 0)
                {
                    parts.Add($"{name}={text}");
                }
            }
        }

        var joined = string.Join(", ", parts);
        return joined.Length > 200 ? joined[..200] + "…" : joined;
    }

    /// <summary>JSON 字符串解包：把「内容为 JSON 文本的字符串」还原为 JSON 文本。</summary>
    private static bool TryUnwrapJsonString(string text, out string unwrapped)
    {
        unwrapped = "";
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // 形如 "{\"a\":1}" 的字符串字面量 → 用 JSON 解析去掉外层引号与转义
        if (trimmed[0] == '"')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                trimmed = doc.RootElement.GetString() ?? "";
            }
            catch (JsonException)
            {
                return false;
            }
        }

        trimmed = trimmed.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            unwrapped = trimmed;
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>大小写不敏感取属性（System.Text.Json 默认区分大小写，故自行遍历）。</summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 脱敏 + 截断原始响应片段：敏感字段值替换为 <c>***</c>，裸长串按长度打码，超长截断。
    /// 供诊断日志与异常消息使用（绝不输出 token 明文）。
    /// </summary>
    public static string SanitizeSnippet(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "(空)";
        }

        var text = SensitiveFieldRegex.Replace(body, match =>
        {
            var colon = match.Value.IndexOf(':');
            return colon < 0 ? "\"***\"" : match.Value[..(colon + 1)] + "\"***\"";
        });

        // 无字段名上下文的长串（裸 token）：保留首尾各 4 位便于比对，其余打码
        text = LongTokenLikeRegex.Replace(text, match =>
            match.Value.Length < 32
                ? match.Value
                : $"{match.Value[..4]}***{match.Value[^4..]}");

        text = text.Replace("\r", " ").Replace("\n", " ");
        if (text.Length > SnippetMaxLength)
        {
            var sb = new StringBuilder(text[..SnippetMaxLength]);
            sb.Append("…(已截断)");
            return sb.ToString();
        }

        return text;
    }
}
