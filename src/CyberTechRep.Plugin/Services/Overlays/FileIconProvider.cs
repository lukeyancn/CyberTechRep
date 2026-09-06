using System.Diagnostics.CodeAnalysis;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 文件关联图标提取器（仅 Windows 生效，其他平台/失败返回 null，由视图用通用图标兜底）：
/// 按扩展名缓存 PNG 字节（Icon.ExtractAssociatedIcon 每次调用有 COM/句柄开销）；
/// 提取不到关联图标时回退系统「文档」通用图标。
/// </summary>
internal static class FileIconProvider
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取文件图标的 PNG 字节（32×32）。优先按文件真实路径提取关联图标；
    /// 文件不存在/非 Windows/任何异常 → null（调用方显示通用图标）。
    /// </summary>
    public static bool TryGetIconPng(string? absolutePath, string fileName, [NotNullWhen(true)] out byte[]? png)
    {
        // 缓存键取扩展名：同扩展名图标一致；无扩展名统一走通用图标
        var extension = Path.GetExtension(fileName ?? "");
        var cacheKey = extension.Length == 0 ? "\0generic" : extension;

        lock (Lock)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                png = cached;
                return png is not null;
            }
        }

        byte[]? extracted = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                extracted = ExtractPng(absolutePath, extension);
            }
            catch
            {
                // 图标提取失败（文件不存在/无关联/COM 异常）走通用图标兜底
                extracted = null;
            }
        }

        lock (Lock)
        {
            Cache[cacheKey] = extracted;
            png = extracted;
            return png is not null;
        }
    }

    /// <summary>Icon.ExtractAssociatedIcon（或系统通用文档图标）→ PNG 字节。仅 Windows 调用。</summary>
    private static byte[]? ExtractPng(string? absolutePath, string extension)
    {
        using var icon = absolutePath is not null && File.Exists(absolutePath) && extension.Length > 0
            ? System.Drawing.Icon.ExtractAssociatedIcon(absolutePath)
            : System.Drawing.SystemIcons.Application;

        if (icon is null)
        {
            return null;
        }

        using var bitmap = icon.ToBitmap();
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        return buffer.ToArray();
    }
}
