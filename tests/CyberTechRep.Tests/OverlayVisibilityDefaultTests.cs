using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 悬浮窗出厂默认与可见性持久化语义回归测试：
/// a. 出厂默认：通知 / 作业 / 学科圆圈栏三窗默认 Visible=true（全新安装随宿主显示）；
///    学科文件窗与未绑定学科选择窗保持默认不随宿主显示（按需弹出语义不变）。
/// b. 用户主动隐藏（× 直接 Hide / 设置页关开关）持久化 Visible=false，重启保持隐藏。
/// c. 宿主退出批量关窗（SettingsChangeApplier.StopAsync → NotifyHostStopping 之后发生的
///    IsVisible=false）不得把 Visible=false 写回 settings.json——否则每次正常退出都会把
///    全部悬浮窗关闭态持久化，重启后默认全关（用户实测缺陷）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayVisibilityDefaultTests : IDisposable
{
    private readonly string _dir;

    public OverlayVisibilityDefaultTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "visible-default", Guid.NewGuid().ToString("N"));
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

    /// <summary>轮询等待落盘完成（SyncVisible/SaveAsync 为 fire-and-forget 异步写盘）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public void FactoryDefaults_ThreeWindowsVisibleTrue_OnDemandWindowsFalse()
    {
        var overlays = new AppSettings().Overlays;

        Assert.True(overlays.Notice.Visible);
        Assert.True(overlays.Homework.Visible);
        Assert.True(overlays.Circle.Visible);
        // 按需弹出语义不变：学科文件窗（圆圈栏唤出）与未绑定学科选择窗（管道触发）默认不随宿主显示
        Assert.False(overlays.Files.Visible);
        Assert.False(overlays.Selection.Visible);
    }

    [Fact]
    public Task DirectWindowHide_PersistsVisibleFalse_SurvivesRestart()
    {
        // × 行为：窗口自身 Hide()（不经控制器），控制器经 IsVisible 侦听把 Visible=false
        // 同步回设置并持久化；重启（重新加载 settings.json）后保持隐藏
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var settingsService = new SettingsService(_dir);
            Window? created = null;
            var controller = new SuspensionWindowController(
                _dir,
                settingsService: settingsService,
                windowFactory: _ => created = new Window());

            await controller.ShowAsync(SuspensionWindowController.NoticeKey);
            Assert.True(controller.Settings.Notice.Visible);

            // 模拟窗上「×」：窗口实例直接 Hide（OnHideClick → Hide() 同款路径），
            // 控制器侦听 IsVisible=false 回写 Visible=false
            created!.Hide();
            Assert.False(controller.Settings.Notice.Visible);

            // 落盘完成后模拟重启：新建 SettingsService 读 settings.json，隐藏态保持
            await WaitUntilAsync(() =>
            {
                try
                {
                    return !new SettingsService(_dir).Current.Overlays.Notice.Visible;
                }
                catch
                {
                    return false;
                }
            });
            Assert.False(new SettingsService(_dir).Current.Overlays.Notice.Visible);
        }, CancellationToken.None);
    }

    [Fact]
    public Task HostStopping_SuppressesCloseTimeVisibilityPersist()
    {
        // 宿主退出：StopAsync 通知控制器后，宿主批量关窗触发 IsVisible=false，
        // 不得把 Visible=false 写回设置（否则重启后全部悬浮窗默认全关）
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var settingsService = new SettingsService(_dir);
            var windows = new List<Window>();
            var controller = new SuspensionWindowController(
                _dir,
                settingsService: settingsService,
                windowFactory: _ =>
                {
                    var w = new Window();
                    windows.Add(w);
                    return w;
                });

            await controller.ShowAsync(SuspensionWindowController.NoticeKey);
            await controller.ShowAsync(SuspensionWindowController.HomeworkKey);

            controller.NotifyHostStopping();

            // 模拟宿主 DesktopLifetime.Shutdown() 批量关闭窗口（不经控制器 HideAsync，
            // 走 IsVisible=false 侦听路径——正是缺陷根因所在路径）
            foreach (var w in windows)
            {
                w.Hide();
            }

            // 给可能存在的错误落盘路径留出时间窗，随后验证设置未被改写
            await Task.Delay(300);
            Assert.True(controller.Settings.Notice.Visible);
            Assert.True(controller.Settings.Homework.Visible);

            var reloaded = new SettingsService(_dir).Current.Overlays;
            Assert.True(reloaded.Notice.Visible);
            Assert.True(reloaded.Homework.Visible);
        }, CancellationToken.None);
    }

    [Fact]
    public void SettingsPageCards_DefaultCollapsed_NoExpandedTrueInSource()
    {
        // 卡片/分区统一默认折叠：设置页 AXAML 中不得再出现 IsExpanded="True"
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var pagesDir = Path.Combine(root!, "src", "CyberTechRep.Plugin", "Controls", "SettingsPages");
        Assert.True(Directory.Exists(pagesDir), $"设置页目录不存在：{pagesDir}");

        var offenders = Directory.EnumerateFiles(pagesDir, "*.axaml")
            .Where(f => File.ReadAllText(f).Contains("IsExpanded=\"True\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"以下设置页存在默认展开的卡片：{string.Join(", ", offenders)}");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CyberTechRep.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        return null;
    }
}
