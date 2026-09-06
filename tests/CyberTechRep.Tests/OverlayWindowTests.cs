using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>模块 6：悬浮窗多屏几何纯函数测试（IsOnScreen 边界计算 / 复位 / DPI 换算）。</summary>
public sealed class OverlayGeometryTests
{
    private static readonly ScreenRect Screen1920 =
        new(0, 0, 1920, 1080);

    private static readonly ScreenRect ScreenSecond =
        new(1920, 0, 1920, 1080);

    [Fact]
    public void WindowFullyInsideScreen_IsOnScreen()
    {
        Assert.True(OverlayGeometry.IsOnScreen(40, 40, 320, 480, [Screen1920]));
    }

    [Fact]
    public void WindowFullyOutsideAllScreens_IsNotOnScreen()
    {
        Assert.False(OverlayGeometry.IsOnScreen(4000, 2000, 320, 480, [Screen1920, ScreenSecond]));
    }

    [Fact]
    public void WindowMostlyDraggedOut_IsNotOnScreen()
    {
        // 窗口 320 宽，只有 40 DIP 可见（占比 12.5% < 15% 阈值）→ 视为拖出屏幕
        var x = ScreenSecond.X + ScreenSecond.Width - 40;
        Assert.False(OverlayGeometry.IsOnScreen(x, 100, 320, 480, [Screen1920, ScreenSecond]));
    }

    [Fact]
    public void WindowStraddlingTwoScreens_IsOnScreen()
    {
        Assert.True(OverlayGeometry.IsOnScreen(1800, 100, 320, 480, [Screen1920, ScreenSecond]));
    }

    [Fact]
    public void EmptyScreenList_FailsSafeAsOnScreen()
    {
        // 无屏幕信息时不误判拖出（防御性兜底）
        Assert.True(OverlayGeometry.IsOnScreen(4000, 2000, 320, 480, []));
    }

    [Fact]
    public void ZeroSizeWindow_IsNotOnScreen()
    {
        Assert.False(OverlayGeometry.IsOnScreen(0, 0, 0, 480, [Screen1920]));
    }

    [Fact]
    public void ClampToScreen_PullsWindowBackInside()
    {
        var (x, y) = OverlayGeometry.ClampToScreen(3000, 2000, 320, 480, Screen1920);

        Assert.InRange(x, 0, 1920 - 320);
        Assert.InRange(y, 0, 1080 - 480);
    }

    [Fact]
    public void FromPixelRect_ConvertsToLogicalDip()
    {
        var dip = OverlayGeometry.FromPixelRect(new Avalonia.PixelRect(0, 0, 3840, 2160), scaling: 2.0);

        Assert.Equal(1920, dip.Width);
        Assert.Equal(1080, dip.Height);
    }
}

/// <summary>模块 6：SuspensionWindowController 逻辑层测试（无 UI 平台：设置持久化 / 复位 / 参数校验）。</summary>
public sealed class SuspensionWindowControllerTests : IDisposable
{
    private readonly string _dir;

    public SuspensionWindowControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "overlays", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task ApplySettings_PersistsAcrossInstances()
    {
        var controller = new SuspensionWindowController(_dir);

        await controller.ApplySettingsAsync("notice", new OverlayWindowSettings
        {
            X = 123.5,
            Y = 77,
            Width = 300,
            Height = 400,
            Opacity = 0.5,
            FontSize = 18
        });

        var reloaded = new SuspensionWindowController(_dir);
        var notice = reloaded.Settings.Notice;
        Assert.Equal(123.5, notice.X);
        Assert.Equal(77, notice.Y);
        Assert.Equal(0.5, notice.Opacity);
        Assert.Equal(18, notice.FontSize);

        // 作业窗设置互不影响
        Assert.Equal(new OverlayWindowSettings().X, reloaded.Settings.Homework.X);
    }

    [Fact]
    public async Task ResetPosition_RestoresDefaultAndPersists()
    {
        var controller = new SuspensionWindowController(_dir);
        await controller.ApplySettingsAsync("homework", new OverlayWindowSettings { X = 2500, Y = 1400 });

        await controller.ResetPositionAsync("homework");

        var reloaded = new SuspensionWindowController(_dir);
        Assert.Equal(SuspensionWindowController.DefaultX, reloaded.Settings.Homework.X);
        Assert.Equal(SuspensionWindowController.DefaultY, reloaded.Settings.Homework.Y);
    }

    [Fact]
    public async Task UnknownKey_IsSafeNoOp()
    {
        var controller = new SuspensionWindowController(_dir);

        await controller.ApplySettingsAsync("unknown", new OverlayWindowSettings());
        await controller.ShowAsync("unknown");
        await controller.HideAsync("unknown");
        await controller.ResetPositionAsync("unknown");

        Assert.False(controller.IsOnScreen("unknown"));
    }

    [Fact]
    public async Task IsOnScreen_WithoutPlatform_TreatsAsOnScreen()
    {
        // 窗口未创建（无 Avalonia 平台信息）：按持久化设置兜底判定为在屏，不误触发复位
        var controller = new SuspensionWindowController(_dir);
        await controller.ApplySettingsAsync("notice", new OverlayWindowSettings { X = 40, Y = 40 });

        Assert.True(controller.IsOnScreen("notice"));
    }

    [Fact]
    public async Task SettingsPersisted_RaisedAfterApply()
    {
        var controller = new SuspensionWindowController(_dir);
        var raised = 0;
        controller.SettingsPersisted += (_, _) => raised++;

        await controller.ApplySettingsAsync("notice", new OverlayWindowSettings { X = 10 });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Settings_ReturnsDefensiveCopy()
    {
        var controller = new SuspensionWindowController(_dir);

        controller.Settings.Notice.X = 999;

        Assert.NotEqual(999, controller.Settings.Notice.X);
    }
}
