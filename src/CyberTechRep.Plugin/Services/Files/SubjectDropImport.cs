using Avalonia.Input;
using Avalonia.Platform.Storage;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.Files;

/// <summary>
/// 拖放导入结果（供悬浮窗汇总展示，非致命失败以文本反馈，不抛异常打断 UI）。
/// </summary>
public enum SubjectDropImportOutcome
{
    /// <summary>已复制归档（源文件保留）。</summary>
    Imported,

    /// <summary>内容与已归档文件重复（MD5 去重，与下载路径行为一致），未重复入库。</summary>
    Duplicate,

    /// <summary>跳过（源不存在/零字节/已在归档库内等），非致命。</summary>
    Skipped,

    /// <summary>失败（非法文件名/超限/IO 错误等），非致命。</summary>
    Failed
}

/// <summary>单文件拖放导入结果。</summary>
public sealed record SubjectDropImportResult(SubjectDropImportOutcome Outcome, string Message, FileRecord? Record)
{
    public static SubjectDropImportResult Imported(FileRecord record) =>
        new(SubjectDropImportOutcome.Imported, "已复制归档", record);

    public static SubjectDropImportResult Duplicate(string message, FileRecord record) =>
        new(SubjectDropImportOutcome.Duplicate, message, record);

    public static SubjectDropImportResult Skipped(string message) =>
        new(SubjectDropImportOutcome.Skipped, message, null);

    public static SubjectDropImportResult Failed(string message) =>
        new(SubjectDropImportOutcome.Failed, message, null);
}

/// <summary>
/// 文件拖放导入契约（由 <see cref="FilePipelineService"/> 实现，与下载共用同一 files.json
/// 记录库与归档目录布局，保证元数据格式零分叉）。
/// </summary>
public interface IFileImportService
{
    /// <summary>
    /// 把磁盘上的一个源文件复制归档到指定学科（Copy 语义：源文件必须保持原位不被移动）。
    /// 任何失败/跳过都以 <see cref="SubjectDropImportResult"/> 返回，不抛异常。
    /// </summary>
    Task<SubjectDropImportResult> ImportAsync(string sourcePath, string subject, CancellationToken ct = default);
}

/// <summary>
/// 拖放导入纯逻辑（可脱离 UI 测试）：
/// <para>① 负载路径提取——优先 Avalonia <see cref="DataFormats.Files"/>（资源管理器原生文件），
/// 回退 <see cref="DataFormats.FileNames"/> 文本格式（浏览器/压缩包工具拖出虚拟文件时的
/// 常见降级负载）；两者都取不到 → 空列表（调用方展示非致命提示）。</para>
/// <para>② 同名冲突策略（确定性、显式）——目标目录已存在同名文件时按
/// <c>name (2).ext</c>、<c>name (3).ext</c>… 顺延取第一个空闲名，绝不静默覆盖已有文件。</para>
/// </summary>
public static class SubjectDropImport
{
    /// <summary>
    /// 从拖放负载提取全部文件路径（多文件支持）。非文件负载 / 取路径异常一律返回空列表（不抛）。
    /// </summary>
    public static IReadOnlyList<string> ExtractFilePaths(IDataObject? data)
    {
        if (data is null)
        {
            return [];
        }

        try
        {
            // ① 原生文件拖放（资源管理器）：DataFormats.Files 携带 IStorageItem，取本地文件系统路径
            var fromItems = data.GetFiles()
                ?.Select(item => item.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .OfType<string>()
                .ToList();
            if (fromItems is { Count: > 0 })
            {
                return Sanitize(fromItems);
            }

            // ② 文本回退（浏览器下载链接/压缩包虚拟文件等以 FileNames 文本格式携带路径）
            if (data.Contains(DataFormats.FileNames) && data.Get(DataFormats.FileNames) is { } raw)
            {
                IEnumerable<string> parts = raw switch
                {
                    string text => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    IEnumerable<string> list => list,
                    _ => []
                };
                return Sanitize(parts);
            }
        }
        catch
        {
            // 非文件负载（纯文本/位图等）读取异常按「无可导入路径」处理，不抛
        }

        return [];
    }

    /// <summary>负载是否包含可提取的文件路径（DragOver 判定 Copy 效果用）。</summary>
    public static bool HasFiles(IDataObject? data) => ExtractFilePaths(data).Count > 0;

    private static IReadOnlyList<string> Sanitize(IEnumerable<string> paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>第 n 个冲突后缀名：<c>name (n).ext</c>（无扩展名文件为 <c>name (n)</c>）。</summary>
    public static string WithConflictSuffix(string fileName, int n) =>
        $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}";

    /// <summary>
    /// 解析目标目录内的空闲文件名：desiredName 不冲突则原样返回；冲突则按
    /// <see cref="WithConflictSuffix"/> 从 (2) 起顺延。不触碰文件系统时可注入 exists 判定（单测用）。
    /// 顺延 998 次仍全部冲突（理论不可达）时返回原名兜底。
    /// </summary>
    public static string ResolveAvailableName(string directory, string desiredName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!exists(Path.Combine(directory, desiredName)))
        {
            return desiredName;
        }

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, WithConflictSuffix(desiredName, n));
            if (!exists(candidate))
            {
                return Path.GetFileName(candidate);
            }
        }

        return desiredName;
    }

    /// <summary>
    /// 批量导入（悬浮窗 Drop 入口）：逐个调用 <see cref="IFileImportService.ImportAsync"/>。
    /// 单文件失败不中断其余文件（每条结果独立），整体在线程池执行避免大文件/大批量卡 UI 线程。
    /// </summary>
    public static async Task<IReadOnlyList<(string Path, SubjectDropImportResult Result)>> ImportAllAsync(
        IFileImportService importer, IReadOnlyList<string> paths, string subject)
    {
        var results = new List<(string, SubjectDropImportResult)>(paths.Count);
        foreach (var path in paths)
        {
            results.Add((path, await importer.ImportAsync(path, subject)));
        }

        return results;
    }
}
