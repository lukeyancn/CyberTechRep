using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using CyberTechRep.Tests.Support;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// SettingsChangeApplier 启动自动显示链路单测（缺陷「设置页显示已启用但重启后悬浮窗不出现」）：
/// a. LaunchWithHost=true 时按每窗 Visible 决定 Show/Hide；
/// b. LaunchWithHost=false（总开关关闭，设置页可见）时不触碰任何窗口（语义不变，零回归）；
/// c. 单窗应用异常被逐窗隔离：第一个窗失败不得让其余悬浮窗失去本次应用（旧实现整组
///    共享一个 try/catch，是启动不显示的根因之一）；
/// d. 启动延迟重放：首拍因启动竞态失败后，重放会把窗口补显示（幂等，不引起状态抖动）。
/// </summary>
public sealed class SettingsChangeApplierStartupTests : IDisposable
{
    private readonly string _dir;

    public SettingsChangeApplierStartupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "applier-startup", Guid.NewGuid().ToString("N"));
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

    /// <summary>记录 Show/Hide 调用的控制器替身；可指定对某 overlayKey 抛异常模拟窗口创建失败。</summary>
    private sealed class RecordingController : ISuspensionWindowController
    {
        private readonly object _lock = new();

        public List<string> Shown { get; } = [];
        public List<string> Hidden { get; } = [];

        public string? ThrowOnKey { get; set; }

        private void ThrowIfConfigured(string overlayKey)
        {
            if (ThrowOnKey == overlayKey)
            {
                throw new InvalidOperationException($"simulated failure for {overlayKey}");
            }
        }

        public Task ShowAsync(string overlayKey, CancellationToken ct = default)
        {
            ThrowIfConfigured(overlayKey);
            lock (_lock)
            {
                Shown.Add(overlayKey);
            }

            return Task.CompletedTask;
        }

        public Task HideAsync(string overlayKey, CancellationToken ct = default)
        {
            ThrowIfConfigured(overlayKey);
            lock (_lock)
            {
                Hidden.Add(overlayKey);
            }

            return Task.CompletedTask;
        }

        public Task ResetPositionAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task ApplySettingsAsync(string overlayKey, OverlayWindowSettings settings, CancellationToken ct = default)
        {
            ThrowIfConfigured(overlayKey);
            return Task.CompletedTask;
        }

        public bool IsOnScreen(string overlayKey) => true;

        public void NotifyHostStopping()
        {
        }

        public int ShownCount(string overlayKey)
        {
            lock (_lock)
            {
                return Shown.Count(k => string.Equals(k, overlayKey, StringComparison.Ordinal));
            }
        }
    }

    private SettingsService MakeSettings(bool launchWithHost, bool noticeVisible, bool filesVisible)
    {
        var svc = new SettingsService(_dir);
        svc.Current.Overlays.LaunchWithHost = launchWithHost;
        svc.Current.Overlays.Notice.Visible = noticeVisible;
        svc.Current.Overlays.Files.Visible = filesVisible;
        return svc;
    }

    [Fact]
    public async Task StartAsync_LaunchWithHostTrue_ShowsVisibleAndHidesHiddenWindows()
    {
        var svc = MakeSettings(launchWithHost: true, noticeVisible: true, filesVisible: false);
        var controller = new RecordingController();
        using var applier = new SettingsChangeApplier(svc, windowController: controller);

        await applier.StartAsync(CancellationToken.None);

        Assert.Contains(SuspensionWindowController.NoticeKey, controller.Shown);
        Assert.Contains(SuspensionWindowController.FilesKey, controller.Hidden);
    }

    [Fact]
    public async Task StartAsync_LaunchWithHostFalse_DoesNotTouchWindows()
    {
        // 总开关关闭（设置页「随宿主 ClassIsland 启动」）：语义保持——不自动显示也不额外隐藏
        var svc = MakeSettings(launchWithHost: false, noticeVisible: true, filesVisible: false);
        var controller = new RecordingController();
        using var applier = new SettingsChangeApplier(svc, windowController: controller);

        await applier.StartAsync(CancellationToken.None);

        Assert.Empty(controller.Shown);
        Assert.Empty(controller.Hidden);
    }

    [Fact]
    public async Task StartAsync_SingleWindowFailure_DoesNotBlockOtherWindows()
    {
        // 逐窗隔离：notice 应用抛异常时，homework/files 等其余窗仍被应用（旧实现整组中断）
        var svc = MakeSettings(launchWithHost: true, noticeVisible: true, filesVisible: true);
        var controller = new RecordingController { ThrowOnKey = SuspensionWindowController.NoticeKey };
        using var applier = new SettingsChangeApplier(svc, windowController: controller);

        await applier.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(SuspensionWindowController.NoticeKey, controller.Shown);
        Assert.Contains(SuspensionWindowController.HomeworkKey, controller.Shown);
        Assert.Contains(SuspensionWindowController.FilesKey, controller.Shown);
    }

    [Fact]
    public async Task StartAsync_ReapplyAfterDelay_RecoversFromFirstShotFailure()
    {
        // 启动竞态兜底：首拍失败（模拟宿主启动早期窗口创建失败），延迟重放补显示
        var svc = MakeSettings(launchWithHost: true, noticeVisible: true, filesVisible: false);
        var controller = new RecordingController { ThrowOnKey = SuspensionWindowController.NoticeKey };
        using var applier = new SettingsChangeApplier(svc, windowController: controller, startupReapplyDelayMs: 80);

        await applier.StartAsync(CancellationToken.None);
        Assert.Equal(0, controller.ShownCount(SuspensionWindowController.NoticeKey));

        // 重放前解除故障（模拟宿主 UI 就绪），等待延迟重放完成
        controller.ThrowOnKey = null;
        await TestWait.WaitForAsync(() => controller.ShownCount(SuspensionWindowController.NoticeKey) >= 1,
            TimeSpan.FromSeconds(5), "延迟重放应把可见悬浮窗补显示");

        // 重放是幂等的补应用：notice 只被 Show 一次（首拍失败 + 重放成功），不产生额外抖动
        Assert.Equal(1, controller.ShownCount(SuspensionWindowController.NoticeKey));
        Assert.Contains(SuspensionWindowController.FilesKey, controller.Hidden);
    }

    [Fact]
    public async Task StartAsync_NoController_DoesNotThrow()
    {
        var svc = MakeSettings(launchWithHost: true, noticeVisible: true, filesVisible: false);
        using var applier = new SettingsChangeApplier(svc, windowController: null);

        var ex = await Record.ExceptionAsync(() => applier.StartAsync(CancellationToken.None));
        Assert.Null(ex);
    }
}
