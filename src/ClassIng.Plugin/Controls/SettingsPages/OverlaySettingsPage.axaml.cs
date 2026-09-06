using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIng.Plugin.Services.Overlays;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 悬浮窗设置页：通知/作业/学科文件三窗 + 学科圆圈启动器各自 X/Y/宽/高/透明度/字号/置顶/可见
/// + 一键复位 + 开机随宿主；另含圆圈栏排列方向、视图模式、学科顺序与上课联动（预留）设置。
/// 复位按钮：恢复默认位置设置，并在悬浮窗控制器已注册时同步复位窗口位置；
/// 控制器未注册时仅复位设置值。
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
        // 组合框初始选中项与多行顺序编辑框按当前设置回填
        OrientationBox.SelectedIndex = SubjectCircleOrder.IsVertical(Settings.Overlays.SubjectCircle.Orientation) ? 1 : 0;
        ViewModeBox.SelectedIndex = SubjectCircleOrder.IsDetailsView(Settings.Overlays.SubjectCircle.ViewMode) ? 1 : 0;
        OrderTextBox.Text = LinesToText(Settings.Overlays.SubjectCircle.Order);
    }

    private void OnResetNoticeClicked(object? sender, RoutedEventArgs e)
        => ResetWindow(SuspensionWindowController.NoticeKey, () => Settings.Overlays.Notice = new OverlayWindowSettings());

    private void OnResetHomeworkClicked(object? sender, RoutedEventArgs e)
        => ResetWindow(SuspensionWindowController.HomeworkKey, () => Settings.Overlays.Homework = new OverlayWindowSettings());

    private void OnResetFilesClicked(object? sender, RoutedEventArgs e)
        => ResetWindow(SuspensionWindowController.FilesKey,
            () => Settings.Overlays.Files = new OverlayWindowSettings { Visible = false, Width = 360, Height = 520 });

    private void OnResetCircleClicked(object? sender, RoutedEventArgs e)
        => ResetWindow(SuspensionWindowController.CircleKey,
            () => Settings.Overlays.Circle = new OverlayWindowSettings { Visible = true, Width = 64, Height = 440, Opacity = 0.85 });

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow();

    private void OnOrientationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OrientationBox.SelectedItem is ComboBoxItem { Tag: string orientation })
        {
            Settings.Overlays.SubjectCircle.Orientation = orientation;
        }
    }

    private void OnViewModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModeBox.SelectedItem is ComboBoxItem { Tag: string viewMode })
        {
            Settings.Overlays.SubjectCircle.ViewMode = viewMode;
        }
    }

    private void OnOrderLostFocus(object? sender, RoutedEventArgs e)
    {
        // 多行编辑框 → 学科顺序（去空行与首尾空白）；页面关闭/保存时随之持久化并热生效
        Settings.Overlays.SubjectCircle.Order = TextToLines(OrderTextBox.Text);
    }

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
