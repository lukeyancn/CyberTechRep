using Avalonia;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>屏幕矩形（逻辑坐标 DIP）。</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height);

/// <summary>
/// 悬浮窗多屏几何纯函数（无 UI / 无平台依赖，可单测）：
/// IsOnScreen 多屏边界判定、ClampToScreen 拖出屏幕后的就近复位、像素/逻辑坐标换算。
/// </summary>
public static class OverlayGeometry
{
    /// <summary>窗口可见面积占比达到该阈值才算「在屏上」（完全在屏外=0，边缘露一角不判失联）。</summary>
    public const double MinVisibleRatio = 0.15;

    /// <summary>
    /// 窗口矩形是否在任一屏幕可视范围内（跨全部屏幕边界判断）。
    /// 无屏幕信息（屏幕列表为空）时视为可用（无法判定，不误复位）。
    /// </summary>
    public static bool IsOnScreen(
        double x, double y, double width, double height,
        IReadOnlyList<ScreenRect> screens,
        double minVisibleRatio = MinVisibleRatio)
    {
        if (screens.Count == 0)
        {
            return true;
        }

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var windowArea = width * height;
        foreach (var screen in screens)
        {
            var ix1 = Math.Max(x, screen.X);
            var iy1 = Math.Max(y, screen.Y);
            var ix2 = Math.Min(x + width, screen.X + screen.Width);
            var iy2 = Math.Min(y + height, screen.Y + screen.Height);
            if (ix2 <= ix1 || iy2 <= iy1)
            {
                continue;
            }

            var visibleRatio = (ix2 - ix1) * (iy2 - iy1) / windowArea;
            if (visibleRatio >= minVisibleRatio)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把窗口位置限制到指定屏幕内（右/下边缘至少留 8 DIP 可视），返回复位后的逻辑坐标。
    /// 拖出屏幕的一键复位按此规则落到最近屏幕。
    /// </summary>
    public static (double X, double Y) ClampToScreen(
        double x, double y, double width, double height, ScreenRect screen)
    {
        const double edgeMargin = 8;
        var clampedX = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - Math.Min(width, screen.Width) - edgeMargin));
        var clampedY = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - Math.Min(height, screen.Height) - edgeMargin));
        return (clampedX, clampedY);
    }

    /// <summary>像素坐标 → 逻辑坐标（DIP）换算（DPI 适配：Avalonia 全程用逻辑坐标持久化）。</summary>
    public static ScreenRect FromPixelRect(PixelRect pixelRect, double scaling) =>
        scaling > 0
            ? new ScreenRect(pixelRect.X / scaling, pixelRect.Y / scaling, pixelRect.Width / scaling, pixelRect.Height / scaling)
            : new ScreenRect(pixelRect.X, pixelRect.Y, pixelRect.Width, pixelRect.Height);
}
