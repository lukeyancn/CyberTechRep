using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.Stores;

/// <summary>
/// 按天归档与保留期清理（纯函数，可单测）。
/// <para>
/// 归档方案：单文件内按 CreatedAt 的本地日期<b>逻辑分桶</b>（桶 = 本地日期，见 <see cref="BucketOf"/>），
/// 不拆分 notices-YYYY-MM-DD.json 多文件。理由：①旧 notices.json/homework.json 条目自带 CreatedAt，
/// 加载即归桶、零迁移、不丢数据；②沿用既有原子写入 + .bak 损坏恢复机制，无需多文件加载/恢复/跨文件去重；
/// ③按 MessageId 幂等合并逻辑不受影响。
/// </para>
/// <para>清理纪律：清理任务在启动时与每日跨天时执行，只删过期桶，绝不动当天与未读。</para>
/// </summary>
public static class RetentionPolicies
{
    /// <summary>CreatedAt → 归档桶（本地日期）。</summary>
    public static DateOnly BucketOf(DateTimeOffset createdAt) =>
        DateOnly.FromDateTime(createdAt.LocalDateTime.Date);

    /// <summary>
    /// 通知保留判定：
    /// 未读通知无条件保留（不受保留期限制，全部保留）；
    /// 已读通知仅保留当天（NoticesRetentionDays &gt; 0 时启用；0=永久 → 全保留，不做已读清理）。
    /// </summary>
    public static bool ShouldKeepNotice(NoticeItem item, DateOnly today, int retentionDays)
    {
        if (!item.IsRead)
        {
            return true; // 未读：绝不动
        }

        if (retentionDays <= 0)
        {
            return true; // 0=永久：不做清理
        }

        return BucketOf(item.CreatedAt) == today; // 已读仅保留当天
    }

    /// <summary>
    /// 作业保留判定：HomeworkRetentionDays=0=永久（不清理）；
    /// &gt;0 → 保留最近 N 天（含当天，即 桶日期 ≥ 今天-(N-1)），更早的桶整体删除。
    /// </summary>
    public static bool ShouldKeepHomework(HomeworkItem item, DateOnly today, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return true; // 0=永久：不做清理
        }

        var ageDays = today.DayNumber - BucketOf(item.CreatedAt).DayNumber;
        return ageDays < retentionDays;
    }
}
