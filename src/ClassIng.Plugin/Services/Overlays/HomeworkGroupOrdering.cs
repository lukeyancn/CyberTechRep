namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 作业悬浮窗分组顺序纯逻辑助手（可脱离 UI 测试）：
/// 配置顺序（<see cref="ClassIng.Shared.Models.OverlaySettings.HomeworkGroupOrder"/>）优先，
/// 未配置的学科按名称序追加在后面；配置为空时保持现状（全部按名称序）。
/// </summary>
public static class HomeworkGroupOrdering
{
    /// <summary>
    /// 解析作业分组的展示顺序。
    /// ① <paramref name="configuredOrder"/> 中出现、且在 <paramref name="subjects"/> 里存在的学科
    ///    （按其列出顺序，去重、忽略首尾空白、大小写不敏感匹配现有学科）；
    /// ② 其余学科按名称序（CurrentCulture，与原分组排序口径一致）追加。
    /// </summary>
    /// <param name="configuredOrder">用户配置的顺序（可为 null/空/含未知学科，未知项安全忽略）。</param>
    /// <param name="subjects">当前实际出现的学科集合。</param>
    public static IReadOnlyList<string> Sort(IReadOnlyList<string>? configuredOrder, IEnumerable<string> subjects)
    {
        var present = subjects
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<string>(present.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configuredOrder ?? [])
        {
            var name = (entry ?? "").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var match = present.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && used.Add(match))
            {
                result.Add(match);
            }
        }

        result.AddRange(present.Where(s => used.Add(s)).OrderBy(s => s, StringComparer.CurrentCulture));
        return result;
    }
}
