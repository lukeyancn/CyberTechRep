using System.Runtime.Versioning;
using Avalonia;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 集成收口：悬浮窗设置迁移测试（overlays.json 独立文件 → ISettingsService 单一来源）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlaySettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public OverlaySettingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "overlay-migrate", Guid.NewGuid().ToString("N"));
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
    public async Task LegacyOverlaysJson_ImportedIntoSettingsService_AndArchived()
    {
        // 旧版独立文件（模块 5/6 遗留）：非默认值
        var legacy = new OverlaySettings
        {
            Notice = new OverlayWindowSettings { X = 123, Y = 456, Width = 500 },
            Homework = new OverlayWindowSettings { X = 77, Y = 88 },
            LaunchWithHost = false
        };
        var legacyPath = Path.Combine(_dir, SuspensionWindowController.LegacyFileName);
        await File.WriteAllTextAsync(legacyPath, CyberTechRep.Plugin.Services.Stores.JsonStoreFile.Serialize(legacy));

        var settings = new SettingsService(_dir);
        var controller = new SuspensionWindowController(_dir, settingsService: settings);

        // 导入：控制器读到的值来自旧文件
        Assert.Equal(123, controller.Settings.Notice.X);
        Assert.Equal(456, controller.Settings.Notice.Y);
        Assert.Equal(500, controller.Settings.Notice.Width);
        Assert.Equal(77, controller.Settings.Homework.X);
        Assert.False(controller.Settings.LaunchWithHost);

        // 归档：旧文件改名 .migrated，不再并存
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + SuspensionWindowController.MigratedSuffix));

        // ISettingsService 成为唯一来源：settings.json 中已含导入值（等待 fire-and-forget 保存完成）
        for (var i = 0; i < 50 && settings.Current.Overlays.Notice.X != 123; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(123, settings.Current.Overlays.Notice.X);
        Assert.Equal(456, settings.Current.Overlays.Notice.Y);
    }

    [Fact]
    public async Task SettingsBackedController_ApplySettings_WritesBackToSettingsService()
    {
        var settings = new SettingsService(_dir);
        var controller = new SuspensionWindowController(_dir, settingsService: settings);

        var applied = new OverlayWindowSettings { X = 200, Y = 300, Width = 640, Height = 400, Opacity = 0.8 };
        await controller.ApplySettingsAsync(SuspensionWindowController.NoticeKey, applied);

        // 单一来源回写：ISettingsService.Current.Overlays 同步更新
        Assert.Equal(200, settings.Current.Overlays.Notice.X);
        Assert.Equal(300, settings.Current.Overlays.Notice.Y);
        Assert.Equal(640, settings.Current.Overlays.Notice.Width);
        Assert.Equal(0.8, settings.Current.Overlays.Notice.Opacity);
        // 作业窗不受影响
        Assert.Equal(40, settings.Current.Overlays.Homework.X);
    }
}
