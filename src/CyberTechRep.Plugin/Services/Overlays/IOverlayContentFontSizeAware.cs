namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 悬浮窗「内容字号」接收方：把设置里的「字号」显式落到内容控件上。
/// <para>
/// 为什么需要：只把字号设在窗口上时，<b>内容文本不跟随</b>——宿主主题（Fluent）给
/// <c>TextBox</c> 等控件的 ControlTheme 自带 <c>FontSize</c>，其优先级高于窗口级的属性继承，
/// 于是「可选中复制的只读文本」「可编辑的作业文档」这些正文始终按主题字号渲染，
/// 用户在悬浮窗设置里改字号只影响没有显式字号的边角控件。
/// </para>
/// <para>
/// 实现方（通知/作业悬浮窗）用「窗口资源 + 样式动态资源」把字号覆盖到正文控件上：
/// 样式优先级高于 ControlTheme，且滚动中新生成的行也自动生效。
/// 由 <see cref="SuspensionWindowController.ApplyToWindow"/> 在每次应用设置时转交；
/// 未实现本接口的悬浮窗保持既有继承行为不变。
/// </para>
/// </summary>
internal interface IOverlayContentFontSizeAware
{
    /// <summary>应用内容字号（逻辑像素 DIP）。在 UI 线程调用，重复调用幂等。</summary>
    void ApplyContentFontSize(double fontSize);
}
