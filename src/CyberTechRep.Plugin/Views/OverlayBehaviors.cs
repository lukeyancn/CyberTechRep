using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Views;

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

/// <summary>
/// 悬浮窗右上角「⋯」快捷菜单共享构建逻辑（通知/作业/学科文件/圆圈栏四窗复用，行为一致）：
/// 置顶/固定/鼠标穿透三个开关与设置页等价——展开时从设置同步勾选态；切换即时生效
/// （经控制器 <c>ApplySettingsAsync</c> 路径应用到窗口）并回写 <c>ISettingsService.SaveAsync</c>
/// 持久化；设置页修改后经 <c>SettingsChanged</c> 双向同步勾选态（控制器回写由
/// ApplySettingsCoreAsync 的单一来源引用判定防回环，不会死循环）。
/// <para>
/// 回写的目标实例由 <paramref name="liveSettings"/> 提供（必须是 ISettingsService.Current.Overlays
/// 里的活实例，如 <c>() =&gt; settings.Current.Overlays.Homework</c>），保证内存即时生效 + 落盘。
/// </para>
/// </summary>
public sealed class OverlayQuickMenu
{
    private readonly ISettingsService? _settingsService;
    private readonly ISuspensionWindowController? _overlays;
    private readonly string _overlayKey;
    private readonly Func<OverlayWindowSettings> _liveSettings;
    private readonly ToggleSwitch _topmostToggle;
    private readonly ToggleSwitch _pinnedToggle;
    private readonly ToggleSwitch _clickThroughToggle;
    private bool _syncing;

    /// <param name="settingsService">设置单一来源；null 时菜单仍展示但开关只读（无回写路径）。</param>
    /// <param name="overlays">悬浮窗控制器（应用开关到窗口）；null 时同上。</param>
    /// <param name="overlayKey">对应悬浮窗的 overlayKey（notice/homework/files/circle）。</param>
    /// <param name="liveSettings">回写目标的活实例取值器（见类型注释）。</param>
    /// <param name="owner">宿主窗口（订阅 SettingsChanged 的生命周期随之）。</param>
    /// <param name="openAbove">true=菜单向上展开（控件在窗口底部时，如圆圈栏）；默认向下展开。</param>
    public OverlayQuickMenu(
        ISettingsService? settingsService,
        ISuspensionWindowController? overlays,
        string overlayKey,
        Func<OverlayWindowSettings> liveSettings,
        Window owner,
        bool openAbove = false)
    {
        _settingsService = settingsService;
        _overlays = overlays;
        _overlayKey = overlayKey;
        _liveSettings = liveSettings;

        _topmostToggle = MakeToggle("置顶（浮在所有窗口之上）");
        _pinnedToggle = MakeToggle("固定（禁用拖拽缩放）");
        _clickThroughToggle = MakeToggle("鼠标穿透（固定模式下生效）");
        foreach (var toggle in new[] { _topmostToggle, _pinnedToggle, _clickThroughToggle })
        {
            toggle.Click += (_, _) => OnToggleClick();
        }

        var panel = new StackPanel { Width = 236, Spacing = 10, Margin = new Thickness(4) };
        panel.Children.Add(_topmostToggle);
        panel.Children.Add(_pinnedToggle);
        panel.Children.Add(_clickThroughToggle);

        Flyout = new Flyout
        {
            Placement = openAbove ? PlacementMode.TopEdgeAlignedRight : PlacementMode.BottomEdgeAlignedRight,
            Content = panel
        };
        Flyout.Opened += (_, _) => SyncFromSettings();

        // 设置页修改悬浮窗开关后双向同步快捷菜单勾选态（经 SettingsChanged；控制器回写也走该广播）
        if (_settingsService is not null)
        {
            EventHandler<AppSettings> handler = (_, _) => Dispatcher.UIThread.Post(SyncFromSettings);
            _settingsService.SettingsChanged += handler;
            owner.Closed += (_, _) => _settingsService.SettingsChanged -= handler;
        }
    }

    /// <summary>构建好的快捷菜单 Flyout（调用方赋给「⋯」按钮的 Flyout 属性）。</summary>
    public Flyout Flyout { get; }

    private static ToggleSwitch MakeToggle(string content) =>
        new() { Content = content, IsChecked = false };

    /// <summary>快捷菜单勾选态 ← 设置（菜单展开时同步一次；设置页修改后经 SettingsChanged 到达）。</summary>
    internal void SyncFromSettings()
    {
        if (_settingsService is null)
        {
            return;
        }

        SyncFrom(_liveSettings());
    }

    /// <summary>勾选态 ← 指定设置（纯逻辑，可单测；同步期间抑制 Click 回写）。</summary>
    internal void SyncFrom(OverlayWindowSettings settings)
    {
        _syncing = true;
        try
        {
            _topmostToggle.IsChecked = settings.Topmost;
            _pinnedToggle.IsChecked = settings.Pinned;
            _clickThroughToggle.IsChecked = settings.ClickThrough;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 开关点击：回写单一来源（liveSettings 返回的活实例），再经控制器 ApplySettingsAsync
    /// 路径即时应用到窗口，最后 SaveAsync 持久化并广播（设置页勾选态随 SettingsChanged 刷新）。
    /// </summary>
    internal void OnToggleClick()
    {
        if (_syncing || _settingsService is null || _overlays is null)
        {
            return;
        }

        WriteTo(_liveSettings());
        _ = ApplyAsync();
    }

    /// <summary>开关状态 → 设置（纯逻辑，可单测）。</summary>
    internal void WriteTo(OverlayWindowSettings settings)
    {
        settings.Topmost = _topmostToggle.IsChecked == true;
        settings.Pinned = _pinnedToggle.IsChecked == true;
        settings.ClickThrough = _clickThroughToggle.IsChecked == true;
    }

    /// <summary>把当前开关状态经控制器应用并落盘（internal：供单测直接驱动回写链路）。</summary>
    internal async Task ApplyAsync()
    {
        try
        {
            await _overlays!.ApplySettingsAsync(_overlayKey, _liveSettings());
            await _settingsService!.SaveAsync();
        }
        catch
        {
            // 快捷菜单应用失败不中断悬浮窗（内存中状态保留，设置页可再改）
        }
    }
}
