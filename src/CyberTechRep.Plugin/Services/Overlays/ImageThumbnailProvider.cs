using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 图片缩略图提取器（供学科文件悬浮窗大图标/详细列表两种模式显示图片文件预览）：
/// 按扩展名判定图片 → Avalonia <see cref="Bitmap.DecodeToWidth"/> 限定最大像素宽度解码
/// （避免 12MP 原图全尺寸解码吃内存）→ 编码为 PNG 字节，并按「路径 + 宽度」做有上限的
/// 内存缓存（超限整体清空，风格与 <see cref="FileIconProvider"/> 一致）。
/// 文件不存在/非图片/解码失败/任何异常一律返回 false，调用方回落文件关联图标或「📄」，绝不抛。
/// </summary>
internal static class ImageThumbnailProvider
{
    /// <summary>支持缩略图解码的图片扩展名（.svg 为矢量图、无扩展名不参与缩略图）。</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"];

    /// <summary>缓存条目上限（超限整体清空：条目为已压缩 PNG 字节，占用可控，避免无限增长）。</summary>
    private const int CacheCapacity = 300;

    private static readonly object Lock = new();
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按扩展名判断是否图片文件（大小写不敏感；无扩展名/.svg 等返回 false）。</summary>
    public static bool IsImageFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取缩略图 PNG 字节（最大宽度 <paramref name="maxPixelWidth"/>，等比缩放）：
    /// 文件不存在/非图片/参数非法/解码失败/任何异常 → false（<paramref name="png"/> 为 null）。
    /// </summary>
    public static bool TryGetThumbnailPng(string? absolutePath, int maxPixelWidth, [NotNullWhen(true)] out byte[]? png)
    {
        png = null;
        if (string.IsNullOrWhiteSpace(absolutePath) || maxPixelWidth <= 0 || !IsImageFileName(absolutePath))
        {
            return false;
        }

        // 缓存键 = 路径 + 最大宽度（同一图片不同宽度各自缓存；负结果也缓存，避免反复解码失败文件）
        var cacheKey = $"{maxPixelWidth}|{absolutePath}";
        lock (Lock)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                png = cached;
                return png is not null;
            }
        }

        byte[]? encoded = null;
        try
        {
            if (File.Exists(absolutePath))
            {
                using var stream = File.OpenRead(absolutePath);
                using var bitmap = Bitmap.DecodeToWidth(stream, maxPixelWidth);
                using var buffer = new MemoryStream();
                bitmap.Save(buffer); // Avalonia 位图默认 PNG 编码
                encoded = buffer.ToArray();
            }
        }
        catch
        {
            // 解码失败（损坏文件/不支持的图片格式/IO 异常）→ 回落调用方图标兜底
            encoded = null;
        }

        lock (Lock)
        {
            if (Cache.Count >= CacheCapacity)
            {
                Cache.Clear();
            }

            Cache[cacheKey] = encoded;
            png = encoded;
            return png is not null;
        }
    }
}
