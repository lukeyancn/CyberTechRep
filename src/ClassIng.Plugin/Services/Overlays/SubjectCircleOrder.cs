using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 学科圆圈栏纯逻辑助手（可脱离 UI 测试）：
/// 学科集合 = 固定七学科 ∪ files.json 里出现过的学科；
/// 顺序按 <see cref="SubjectCircleBarSettings.Order"/>（未配置的按固定顺序追加）；
/// 另含圆圈「上移/下移」重排与视图模式解析。
/// </summary>
public static class SubjectCircleOrder
{
    /// <summary>固定七学科（与作业悬浮窗「修正学科」候选一致）。</summary>
    public static readonly string[] BaseSubjects = ["语文", "数学", "英语", "物理", "化学", "生物", "其他"];

    /// <summary>
    /// 解析圆圈栏展示顺序：
    /// ① <paramref name="order"/> 中出现过的学科（按其列出顺序，去重）；
    /// ② 未列出的固定七学科（按固定顺序）；
    /// ③ 其余 files.json 中出现过的学科（按名称排序追加）。
    /// </summary>
    /// <param name="order">用户配置的顺序（可为 null/空/含未知学科）。</param>
    /// <param name="discovered">files.json 里出现过的学科名集合。</param>
    public static IReadOnlyList<string> ResolveOrder(IReadOnlyList<string>? order, IEnumerable<string> discovered)
    {
        var known = new LinkedHashSet(StringComparer.Ordinal);
        foreach (var s in BaseSubjects)
        {
            known.Add(s);
        }

        foreach (var s in discovered)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                known.Add(s.Trim());
            }
        }

        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in order ?? [])
        {
            var name = (s ?? "").Trim();
            if (name.Length > 0 && known.Contains(name) && used.Add(name))
            {
                result.Add(name);
            }
        }

        // 未配置的学科按固定七学科 → 其余发现学科（名称序）追加
        foreach (var s in BaseSubjects)
        {
            if (used.Add(s))
            {
                result.Add(s);
            }
        }

        foreach (var s in known)
        {
            if (!BaseSubjects.Contains(s, StringComparer.Ordinal) && used.Add(s))
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>
    /// 在当前展示顺序中把某学科上移/下移（delta&lt;0 上移，delta&gt;0 下移，越界自动收敛到端点）。
    /// 返回重排后的完整顺序（持久化回 <see cref="SubjectCircleBarSettings.Order"/>）。
    /// </summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> subjects, string subject, int delta)
    {
        var list = subjects.ToList();
        var index = list.FindIndex(s => string.Equals(s, subject, StringComparison.Ordinal));
        if (index < 0 || delta == 0)
        {
            return list;
        }

        var target = Math.Clamp(index + delta, 0, list.Count - 1);
        if (target == index)
        {
            return list;
        }

        (list[index], list[target]) = (list[target], list[index]);
        return list;
    }

    /// <summary>视图模式是否为详细列表（非 Icons 值一律按详细列表处理，向前兼容）。</summary>
    public static bool IsDetailsView(string? viewMode) =>
        !string.Equals(viewMode, SubjectCircleBarOptions.ViewModeIcons, StringComparison.OrdinalIgnoreCase);

    /// <summary>排列方向是否为纵向（非 Horizontal 值一律按纵向处理，向前兼容）。</summary>
    public static bool IsVertical(string? orientation) =>
        !string.Equals(orientation, SubjectCircleBarOptions.OrientationHorizontal, StringComparison.OrdinalIgnoreCase);

    /// <summary>保序去重的轻量集合（.NET 8 无 LinkedHashSet，内部实现）。</summary>
    private sealed class LinkedHashSet(IEqualityComparer<string> comparer) : IEnumerable<string>
    {
        private readonly List<string> _items = [];
        private readonly HashSet<string> _set = new(comparer);

        public void Add(string item)
        {
            if (_set.Add(item))
            {
                _items.Add(item);
            }
        }

        public bool Contains(string item) => _set.Contains(item);

        public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
