using System.Runtime.Versioning;
using Avalonia.Controls;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 悬浮窗拖拽门控回归测试（0.2.0 拖拽失效回归）：
/// 「固定」模式默认必须为关（Pinned=false），否则通知/作业/文件悬浮窗与圆圈栏
/// 在默认配置下全部不可拖拽缩放（OverlayBehaviors.Fixed 被误置 true）。
/// 同时覆盖门控写入链路：ApplyToWindow 按 Pinned 设置 OverlayBehaviors.Fixed。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayDragGateTests : IDisposable
{
    private readonly string _dir;

    public OverlayDragGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "drag-gate", Guid.NewGuid().ToString("N"));
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
    public void Pinned_DefaultsFalse_SoOverlaysAreDraggableByDefault()
    {
        // 回归根因：0.2.0 曾把默认值改为 true，导致默认配置下四窗全部禁用拖拽缩放
        Assert.False(new OverlayWindowSettings().Pinned);
    }

    [Fact]
    public void LegacyJsonWithoutPinnedField_LoadsAsDraggable()
    {
        // 升级场景：旧 settings.json 没有 Pinned 字段，反序列化后必须回到可拖拽默认
        const string legacy = """
            {
              "SchemaVersion": 1,
              "Overlays": {
                "Notice": { "X": 100 },
                "Homework": { "Visible": true }
              }
            }
            """;

        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacy);

        Assert.NotNull(loaded);
        Assert.False(loaded!.Overlays.Notice.Pinned);
        Assert.False(loaded.Overlays.Homework.Pinned);
    }

    [Fact]
    public void ExplicitPinnedTrue_RoundTripsAndStaysFixed()
    {
        // 用户显式开启固定：序列化往返后仍为固定（默认值改动不得吞掉用户选择）
        var settings = new AppSettings();
        settings.Overlays.Notice.Pinned = true;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.Overlays.Notice.Pinned);
        Assert.False(roundTripped.Overlays.Homework.Pinned);
    }

    [Fact]
    public Task ApplyToWindow_FixedFollowsPinnedSetting()
    {
        // 构造 Window 须在 Avalonia UI 线程（xunit 并行下经 Headless 会话调度）
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = new Window();
            var settings = new SettingsService(_dir);
            var controller = new SuspensionWindowController(_dir, settingsService: settings);

            // 默认（固定=关）：门控关闭 → 标题栏可拖拽、CanResize 开启
            controller.ApplyToWindow(
                SuspensionWindowController.NoticeKey, window, settings.Current.Overlays.Notice);
            Assert.False(OverlayBehaviors.GetFixed(window));
            Assert.True(window.CanResize);

            // 固定=开：门控生效 → 禁拖拽、禁系统级缩放
            var pinned = new OverlayWindowSettings { Pinned = true };
            controller.ApplyToWindow(SuspensionWindowController.NoticeKey, window, pinned);
            Assert.True(OverlayBehaviors.GetFixed(window));
            Assert.False(window.CanResize);

            // 关回固定=开 → 门控随之解除（热生效）
            controller.ApplyToWindow(
                SuspensionWindowController.NoticeKey, window, settings.Current.Overlays.Notice);
            Assert.False(OverlayBehaviors.GetFixed(window));
            Assert.True(window.CanResize);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task OverlayBehaviors_FixedProperty_DefaultsFalse()
    {
        // 附加属性本体默认必须为 false：未应用过设置的窗口可拖拽
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = new Window();

            Assert.False(OverlayBehaviors.GetFixed(window));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
