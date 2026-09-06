using Avalonia;
using Avalonia.Controls;

namespace ClassIng.Plugin.Views;

/// <summary>
/// 悬浮窗行为附加属性。<see cref="FixedProperty"/>=true 表示窗口处于「固定」模式：
/// 标题栏拖拽与角部缩放手势被禁用（位置大小只能经设置页调整）。
/// 由 <see cref="Services.Overlays.SuspensionWindowController"/> 按设置写入，
/// 两个悬浮窗的指针处理器读取该值决定是否响应拖拽/缩放。
/// </summary>
public static class OverlayBehaviors
{
    /// <summary>固定模式：禁用拖拽与缩放手势。</summary>
    public static readonly AttachedProperty<bool> FixedProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("Fixed", typeof(OverlayBehaviors));

    public static void SetFixed(Window window, bool value) => window.SetValue(FixedProperty, value);

    public static bool GetFixed(Window window) => window.GetValue(FixedProperty);
}
