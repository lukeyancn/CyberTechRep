using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 悬浮窗设置页：两窗各自 X/Y/宽/高/透明度/字号/置顶/可见 + 一键复位 + 开机随宿主。
/// 复位按钮：恢复默认位置设置，并在悬浮窗控制器（模块 5/6）已注册时同步复位窗口位置；
/// 控制器未注册时（当前阶段）仅复位设置值。
/// </summary>
[SettingsPageInfo("classing.settings.overlay", "ClassIng 悬浮窗")]
[Group("classing.settings")]
public partial class OverlaySettingsPage : ClassIngSettingsPageBase
{
    private readonly ISuspensionWindowController? _windowController;

    public OverlaySettingsPage(ISettingsService settingsService,
        ISuspensionWindowController? windowController = null)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        _windowController = windowController;
        InitializeComponent();
    }

    private void OnResetNoticeClicked(object? sender, RoutedEventArgs e)
        => ResetWindow("notice", () => Settings.Overlays.Notice = new OverlayWindowSettings());

    private void OnResetHomeworkClicked(object? sender, RoutedEventArgs e)
        => ResetWindow("homework", () => Settings.Overlays.Homework = new OverlayWindowSettings());

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow();

    private void ResetWindow(string overlayKey, Action applyDefaults)
    {
        applyDefaults();
        SaveNow();

        if (_windowController is not null)
        {
            _ = _windowController.ResetPositionAsync(overlayKey);
        }
    }
}
