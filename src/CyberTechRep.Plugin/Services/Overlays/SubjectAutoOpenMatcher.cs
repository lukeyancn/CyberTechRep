namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 上课联动学科匹配纯逻辑（可脱离 UI/宿主测试）：
/// <para>
/// ① 哨兵值兜底：宿主查不到课表/科目时 <c>CurrentSubject</c> 为哨兵实例
/// （Fallback Name="???"、Empty Name=""、课间 Breaking Name="课间休息"），
/// 这些名字一律视为「无当前课」，不得触发联动；
/// ② 学科名匹配：当前科目名与归档学科段先精确匹配（OrdinalIgnoreCase），
/// 不中再做包含兜底（归档段是科目名的子串，如「数学」⊂「数学(提高班)」；
/// 多个命中取最长段，保证结果确定）。
/// </para>
/// </summary>
public static class SubjectAutoOpenMatcher
{
    /// <summary>宿主哨兵科目名：Fallback="???" 与 Breaking="课间休息"（Empty 为空串，另行判空）。</summary>
    public static readonly string[] SentinelNames = ["???", "课间休息"];

    /// <summary>
    /// 判定是否为「真实正在上的课」的科目名：非空白且不是哨兵名（"???"/"课间休息"）。
    /// </summary>
    public static bool IsRealLessonSubject(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        foreach (var sentinel in SentinelNames)
        {
            if (string.Equals(trimmed, sentinel, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 在归档学科段集合中找到与当前科目名对应的学科：
    /// 先精确匹配（OrdinalIgnoreCase，命中即返回）；不中再包含兜底（归档段 ⊆ 科目名），
    /// 多个命中取最长段；无命中或科目名为哨兵/空时返回 null。
    /// </summary>
    /// <param name="currentSubjectName">宿主当前科目名（可为 null/哨兵名）。</param>
    /// <param name="archivedSubjects">归档路径里出现过的学科段（可为 null 项，内部跳过）。</param>
    public static string? MatchArchivedSubject(string? currentSubjectName, IEnumerable<string?>? archivedSubjects)
    {
        if (!IsRealLessonSubject(currentSubjectName) || archivedSubjects is null)
        {
            return null;
        }

        var current = currentSubjectName!.Trim();
        string? best = null;
        foreach (var raw in archivedSubjects)
        {
            var candidate = raw?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            // 精确匹配优先：立即返回（OrdinalIgnoreCase）
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            // 包含兜底：归档段是科目名的子串（如「数学」⊂「数学(提高班)」），取最长命中
            if (candidate.Length >= (best?.Length ?? 0)
                && current.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                best = candidate;
            }
        }

        return best;
    }
}
