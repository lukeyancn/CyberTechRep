using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>
/// 学科词表可视化编辑器校验纯逻辑（可脱离 UI 测试）：
/// 防呆校验（空学科名 / 非法字符 / 重复学科 / 空关键词）与规范化（去空白、去重关键词）。
/// 非法字符校验口径与文件管道 <c>FilePipelineService</c> 的文件名安全校验一致
/// （学科名同时用作归档目录名，必须路径安全）。
/// </summary>
public static class SubjectRulesEditorLogic
{
    /// <summary>
    /// 校验一批学科规则，返回错误消息列表（空列表 = 通过）。
    /// 校验项：①学科名为空；②学科名包含路径分隔符 / 路径穿越片段 / 非法文件名字符；
    /// ③学科名重复（忽略大小写与首尾空白）；④没有任何关键词（空白行已剔除后计数）。
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<SubjectRule>? rules)
    {
        var errors = new List<string>();
        if (rules is null)
        {
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var rule in rules)
        {
            index++;
            if (rule is null)
            {
                continue;
            }

            var subject = (rule.Subject ?? "").Trim();
            if (subject.Length == 0)
            {
                errors.Add($"第 {index} 条：学科名不能为空。");
                continue;
            }

            if (IsUnsafeSubjectName(subject))
            {
                errors.Add($"学科「{subject}」包含路径分隔符或非法字符（学科名同时用作归档目录名）。");
                continue;
            }

            if (!seen.Add(subject))
            {
                errors.Add($"学科「{subject}」重复（忽略大小写）。");
            }

            var keywordCount = (rule.Keywords ?? [])
                .Count(k => !string.IsNullOrWhiteSpace(k));
            if (keywordCount == 0)
            {
                errors.Add($"学科「{subject}」没有任何关键词（空关键词行已剔除后计数），该学科将无法被命中。");
            }
        }

        return errors;
    }

    /// <summary>
    /// 规范化一批学科规则：学科名与关键词去首尾空白、剔除空白关键词行、
    /// 关键词去重（忽略大小写）、剔除无效条目；顺序保持不变。
    /// </summary>
    public static IReadOnlyList<SubjectRule> Normalize(IEnumerable<SubjectRule>? rules) =>
        (rules ?? [])
        .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Subject))
        .Select(r => new SubjectRule
        {
            Subject = r.Subject.Trim(),
            Keywords = (r.Keywords ?? [])
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Priority = r.Priority
        })
        .ToList();

    /// <summary>学科名路径安全校验（与文件管道文件名校验同口径）。</summary>
    public static bool IsUnsafeSubjectName(string name) =>
        string.IsNullOrWhiteSpace(name)
        || name.Contains('/')
        || name.Contains('\\')
        || name.Contains("..")
        || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
}
