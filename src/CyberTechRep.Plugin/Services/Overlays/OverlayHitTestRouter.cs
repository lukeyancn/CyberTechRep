using Avalonia;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 悬浮窗命中测试分流纯逻辑（需求 6）：鼠标穿透开启时，按「可交互区域」决定这次点击是
/// 被悬浮窗接收（可选中复制）还是穿透到下方窗口。
/// <para>
/// 抽成无 Win32 依赖的纯函数以便单测覆盖——真正的 <c>WM_NCHITTEST</c> / <c>ScreenToClient</c>
/// 需要真实窗口句柄与消息循环，无显示设备的测试环境驱动不了；这与
/// <see cref="DesktopLevelZProbe"/>（Z 序判定纯逻辑）采用同一模式。
/// </para>
/// <para>
/// 坐标口径：区域与点均为<b>窗口客户区逻辑坐标（DIP）</b>；<see cref="UnpackScreenPoint"/>
/// 负责把 <c>lParam</c> 拆成屏幕像素坐标，<see cref="ToClientLogical"/> 负责按缩放系数换算。
/// </para>
/// </summary>
internal static class OverlayHitTestRouter
{
    /// <summary>
    /// 客户区逻辑坐标点是否落在可交互区域内。空/负尺寸区域一律视为「无可交互区域」→ 整窗穿透
    /// （列表为空或未完成布局时的既有契约：不因 UI 异常导致窗口「点不透」）。
    /// </summary>
    public static bool IsInteractive(Rect region, Point clientLogicalPoint)
        => region.Width > 0 && region.Height > 0 && region.Contains(clientLogicalPoint);

    /// <summary>客户区像素坐标 → 逻辑坐标（DIP）。缩放系数非法（&lt;= 0）时按 1.0 兜底。</summary>
    public static Point ToClientLogical(int clientPixelX, int clientPixelY, double scaling)
    {
        var factor = scaling > 0 ? scaling : 1.0;
        return new Point(clientPixelX / factor, clientPixelY / factor);
    }

    /// <summary>
    /// <c>WM_NCHITTEST</c> 的 <c>lParam</c> 拆包：低 16 位为 X、高 16 位为 Y（各自带符号，
    /// 支持多显示器负坐标）。截断到 short 是有意的——与 Win32 打包方式一致。
    /// </summary>
    public static (int X, int Y) UnpackScreenPoint(IntPtr lParam)
    {
        var packed = (long)lParam;
        return (unchecked((short)packed), unchecked((short)(packed >> 16)));
    }
}
