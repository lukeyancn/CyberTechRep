using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>学科文件悬浮窗「按日期分段」的分组视图（组头为 yyyy-MM-dd 日期文本）。</summary>
public sealed class SubjectFileDateGroup
{
    public required string Date { get; init; }

    public required IReadOnlyList<FileRecord> Items { get; init; }
}

/// <summary>
/// 学科文件悬浮窗数据查询纯逻辑（可脱离 UI 测试）：
/// 从 <see cref="IFilePipelineService.GetRecordsAsync"/> 的记录中筛出指定学科的已归档文件
/// （<see cref="FileStatus.Archived"/>，按 <see cref="FileRecord.ArchivedRelativePath"/> 的
/// 学科段过滤），按时间倒序并按归档日期（yyyy-MM-dd）分组。
/// </summary>
public static class SubjectFilesQuery
{
    /// <summary>
    /// 过滤某学科的已归档文件：Status==Archived 且 ArchivedRelativePath 首段（学科目录，
    /// 兼容 / 与 \ 分隔）与 subject 一致。返回按 CreatedAt 倒序（新文件在前）。
    /// </summary>
    public static IReadOnlyList<FileRecord> FilterBySubject(IEnumerable<FileRecord> records, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return [];
        }

        return records
            .Where(r => r.Status == FileStatus.Archived
                && string.Equals(ExtractSubject(r.ArchivedRelativePath), subject, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// 按归档日期（yyyy-MM-dd）分段：组间按日期倒序（无法解析日期的记录归入「未知日期」组排最后），
    /// 组内保持传入顺序（推荐传入已按时间倒序的记录）。
    /// </summary>
    public static IReadOnlyList<SubjectFileDateGroup> GroupByDate(IEnumerable<FileRecord> records)
    {
        var groups = new List<SubjectFileDateGroup>();
        var index = new Dictionary<string, List<FileRecord>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var date = ExtractDate(record) ?? "未知日期";
            if (!index.TryGetValue(date, out var bucket))
            {
                bucket = [];
                index[date] = bucket;
                groups.Add(new SubjectFileDateGroup { Date = date, Items = bucket });
            }

            bucket.Add(record);
        }

        // 组间按日期倒序（yyyy-MM-dd 文本序与时间序一致）；「未知日期」组排最后
        groups.Sort((a, b) =>
        {
            var aDate = ParseDateOrMin(a.Date);
            var bDate = ParseDateOrMin(b.Date);
            return aDate == bDate ? string.CompareOrdinal(a.Date, b.Date) : bDate.CompareTo(aDate);
        });
        return groups;
    }

    private static DateTime ParseDateOrMin(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : DateTime.MinValue;

    /// <summary>
    /// 归档相对路径首段即学科目录（形如 "&lt;学科&gt;/&lt;yyyy-MM-dd&gt;/&lt;文件名&gt;"，与文件管道
    /// ReassignSubject 的首段比较语义一致）。首段是日期形（未按学科分组归档）时返回 null（不可归属学科）。
    /// </summary>
    public static string? ExtractSubject(string? archivedRelativePath)
    {
        var segments = SplitSegments(archivedRelativePath);
        if (segments.Length < 2)
        {
            return null;
        }

        return DateTime.TryParseExact(
            segments[0], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _)
            ? null
            : segments[0];
    }

    /// <summary>归档日期段：路径倒数第二段形如 yyyy-MM-dd 时返回，否则 null。</summary>
    public static string? ExtractDate(FileRecord record)
    {
        var segments = SplitSegments(record.ArchivedRelativePath);
        if (segments.Length >= 2 && DateTime.TryParseExact(
                segments[^2], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _))
        {
            return segments[^2];
        }

        return record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd");
    }

    private static string[] SplitSegments(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
