using Avalonia;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 悬浮窗「可交互区域」提供者（需求 6）：鼠标穿透开启时，仅该区域接收鼠标，
/// 区域外仍然穿透（点击落到下方窗口）。
/// <para>
/// 实现方（如通知悬浮窗）返回需要保留交互的内容区（可选中复制的只读文本），
/// 坐标为该窗口客户区逻辑坐标（DIP）；返回空矩形表示「无可交互区域，整窗穿透」。
/// </para>
/// <para>
/// 仅 Windows 生效（<see cref="DesktopLevelPinner"/> 经 <c>WM_NCHITTEST</c> 实现）；
/// 未实现本接口的悬浮窗保持既有「整窗穿透」契约不变。
/// </para>
/// </summary>
internal interface IOverlayInteractiveRegionProvider
{
    /// <summary>可交互区域（窗口客户区逻辑坐标；空矩形 = 整窗穿透）。须可在任意线程被高频调用（只读缓存值）。</summary>
    Rect GetInteractiveRegion();
}
