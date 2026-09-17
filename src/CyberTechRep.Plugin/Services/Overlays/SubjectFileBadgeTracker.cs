namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 学科圆圈「有新文件」未读标记器（纯逻辑，线程安全；仅会话内有效，不持久化）：
/// 文件管道归档完成（<c>FileUpdated</c> 且 <c>Status==Archived</c>）时按学科
/// <see cref="MarkUnseen"/> 标记，用户在圆圈栏点开该学科文件悬浮窗或上课联动打开该学科时
/// <see cref="Clear"/> 清除（对应圆圈右上角小红点）。
/// <para>
/// 学科名比较用 <see cref="StringComparer.OrdinalIgnoreCase"/>（与圆圈栏开关判断的大小写
/// 语义无关，仅用于「同一学科」归并），首尾空白忽略；空/未分类学科不做特殊跳过，
/// 同样按普通学科参与标记与查询（表现：标记能成功、<see cref="IsUnseen"/> 返回 true，
/// 但没有对应圆圈项可显示，也就不会被用户点到清除——见测试内的显式说明）。
/// </para>
/// </summary>
public sealed class SubjectFileBadgeTracker
{
    private readonly object _lock = new();
    private readonly HashSet<string> _unseen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标记某学科有新文件（重复标记幂等；subject 为 null/空白按空学科键处理）。</summary>
    public void MarkUnseen(string? subject)
    {
        var key = Normalize(subject);
        lock (_lock)
        {
            _unseen.Add(key);
        }
    }

    /// <summary>清除某学科的未读标记（无标记时为 no-op）。</summary>
    public void Clear(string? subject)
    {
        var key = Normalize(subject);
        lock (_lock)
        {
            _unseen.Remove(key);
        }
    }

    /// <summary>某学科是否有未读新文件（空/未分类学科同样按普通学科判断）。</summary>
    public bool IsUnseen(string? subject)
    {
        var key = Normalize(subject);
        lock (_lock)
        {
            return _unseen.Contains(key);
        }
    }

    /// <summary>当前全部未读学科（快照，供圆圈列表重建时合并标记与诊断用）。</summary>
    public IReadOnlyList<string> UnseenSubjects
    {
        get
        {
            lock (_lock)
            {
                return _unseen.ToList();
            }
        }
    }

    /// <summary>标记键归一化：null/空白 → 空串键；首尾空白忽略。</summary>
    private static string Normalize(string? subject) => subject?.Trim() ?? string.Empty;
}
