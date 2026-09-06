namespace ClassIng.Plugin.Services.Stores;

/// <summary>
/// 通知学科前缀（纯函数，可单测）：已绑定学科映射的发送者发出通知时，
/// 通知内容前附加「学科名称：」前缀（<see cref="Separator"/>，中文冒号）。
/// 无映射不加；内容已带同学科前缀时不重复添加（幂等，重放/重写安全）。
/// </summary>
public static class NoticeSubjectPrefixer
{
    /// <summary>前缀分隔符（中文冒号）。</summary>
    public const char Separator = '：';

    /// <summary>内容是否已带「{subject}{Separator}」前缀。</summary>
    public static bool HasPrefix(string? content, string? subject) =>
        !string.IsNullOrWhiteSpace(content)
        && !string.IsNullOrWhiteSpace(subject)
        && content.StartsWith(subject + Separator, StringComparison.Ordinal);

    /// <summary>
    /// 附加学科前缀；学科为空/内容为空/已有前缀时原样返回（不重复添加）。
    /// </summary>
    public static string Apply(string content, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrEmpty(content))
        {
            return content;
        }

        return HasPrefix(content, subject) ? content : subject + Separator + content;
    }
}
