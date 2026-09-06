using System.Runtime.Versioning;
using Avalonia.Controls;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 悬浮窗排错回归测试（0.2.x 用户实测缺陷 a/b）：
/// a. 学科文件悬浮窗拖动时自动消失——根因：圆圈栏唤出走 ShowAsync 但 Visible 仍为默认 false，
///    拖拽回写 → SettingsChanged → SettingsChangeApplier 按 Visible=false Hide。
///    修复：控制器 Show/Hide（含窗口自身 Hide()）把可见性同步回设置。
/// b. 调整后位置更新但大小未更新——根因：Bounds 捕获路径在隐藏窗口上取到过渡尺寸 + 高频
///    落盘广播回放与手势打架。修复：隐藏中不捕获 + 落盘防抖 + ApplyToWindow 逐字段守卫。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayVisibilitySyncTests : IDisposable
{
    private readonly string _dir;

    public OverlayVisibilitySyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "visible-sync", Guid.NewGuid().ToString("N"));
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
    public void Files_DefaultVisibleFalse()
    {
        // 前提：文件悬浮窗默认不随宿主显示（这正是缺陷 a 的触发条件）
        Assert.False(new OverlaySettings().Files.Visible);
    }

    [Fact]
    public Task ShowAsync_SyncsVisibleTrue_HideAsync_SyncsFalse()
    {
        // 缺陷 a 修复：不经设置页的 Show/Hide 也要把 Visible 同步回设置，
        // 否则任一次设置广播都会按残留 Visible 把窗隐藏/复活
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var controller = new SuspensionWindowController(
                _dir,
                settingsService: new SettingsService(_dir),
                windowFactory: _ => new Window());

            await controller.ShowAsync(SuspensionWindowController.FilesKey);
            Assert.True(controller.Settings.Files.Visible);

            await controller.HideAsync(SuspensionWindowController.FilesKey);
            Assert.False(controller.Settings.Files.Visible);
        }, CancellationToken.None);
    }

    [Fact]
    public Task SyncVisible_OnlyPersists_OnRealChange()
    {
        // 防广播回环：状态不变时不落盘（SettingsPersisted 不应再次触发）
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var controller = new SuspensionWindowController(
                _dir,
                settingsService: new SettingsService(_dir),
                windowFactory: _ => new Window());
            var persisted = 0;
            controller.SettingsPersisted += (_, _) => Interlocked.Increment(ref persisted);

            await controller.ShowAsync(SuspensionWindowController.FilesKey);
            var afterShow = Volatile.Read(ref persisted);

            // 重复 Show（已可见）：Visible 已是 true，不得再次写盘广播
            await controller.ShowAsync(SuspensionWindowController.FilesKey);

            Assert.Equal(afterShow, Volatile.Read(ref persisted));
        }, CancellationToken.None);
    }

    [Fact]
    public Task GeometryCapture_UpdatesSizeInMemory_WhileVisible()
    {
        // 缺陷 b 捕获链路：窗口可见时调整尺寸，Bounds 变化必须即时反映到内存设置
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var window = new Window { Width = 320, Height = 480 };
            var controller = new SuspensionWindowController(
                _dir,
                settingsService: new SettingsService(_dir),
                windowFactory: _ => window);

            await controller.ShowAsync(SuspensionWindowController.NoticeKey);
            window.Width = 500;
            window.UpdateLayout();

            Assert.Equal(500, controller.Settings.Notice.Width, 2);
        }, CancellationToken.None);
    }

    [Fact]
    public Task GeometryCapture_IgnoresHiddenWindow()
    {
        // 隐藏中的窗口不回写：× 关闭瞬间的过渡 Bounds 不得污染持久化值
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var window = new Window { Width = 320, Height = 480 };
            var controller = new SuspensionWindowController(
                _dir,
                settingsService: new SettingsService(_dir),
                windowFactory: _ => window);

            await controller.ShowAsync(SuspensionWindowController.NoticeKey);
            Assert.Equal(320, controller.Settings.Notice.Width, 2);

            await controller.HideAsync(SuspensionWindowController.NoticeKey);
            window.Width = 900;
            window.UpdateLayout();

            // 隐藏中的尺寸变化被忽略
            Assert.Equal(320, controller.Settings.Notice.Width, 2);
        }, CancellationToken.None);
    }

    [Fact]
    public void ApplyToWindow_SkipsUnchangedFields_KeepsPinnedGate()
    {
        // 逐字段守卫不改变「固定」门控语义
        AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = new Window();
            var settings = new SettingsService(_dir);
            var controller = new SuspensionWindowController(_dir, settingsService: settings);

            var pinned = new OverlayWindowSettings { Pinned = true, ClickThrough = true };
            controller.ApplyToWindow(SuspensionWindowController.NoticeKey, window, pinned);
            Assert.True(OverlayBehaviors.GetFixed(window));
            Assert.False(window.CanResize);
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>通知/作业悬浮窗「仅当天」与未读/已读视图纯逻辑测试。</summary>
public sealed class OverlayListFilterTests
{
    private static NoticeItem Item(string messageId, DateTimeOffset createdAt, bool isRead) => new()
    {
        MessageId = messageId,
        Content = messageId,
        CreatedAt = createdAt,
        IsRead = isRead
    };

    /// <summary>按测试机器本地时区构造「本地墙上时间」时间戳，保证断言在任意时区（含 CI 的 UTC）成立。</summary>
    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute = 0)
    {
        var naive = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(naive, TimeZoneInfo.Local.GetUtcOffset(naive));
    }

    [Fact]
    public void SelectReadToday_KeepsOnlyTodaysRead()
    {
        var today = new DateOnly(2026, 9, 6);
        var todayRead = Item("t1", LocalTime(2026, 9, 6, 8), isRead: true);
        var todayUnread = Item("t2", LocalTime(2026, 9, 6, 9), isRead: false);
        var yesterdayRead = Item("y1", LocalTime(2026, 9, 5, 20), isRead: true);

        var result = NoticeViewFilter.SelectReadToday([todayUnread, yesterdayRead, todayRead], today);

        Assert.Single(result, todayRead);
    }

    [Fact]
    public void SelectReadToday_EmptyWhenNothingReadToday()
    {
        var today = new DateOnly(2026, 9, 6);
        var yesterdayRead = Item("y1", LocalTime(2026, 9, 5, 20), isRead: true);

        Assert.Empty(NoticeViewFilter.SelectReadToday([yesterdayRead], today));
    }

    [Fact]
    public void FormatTime_ReadViewShowsClockOnly_UnreadShowsDateAndClock()
    {
        var item = Item("t1", LocalTime(2026, 9, 6, 8, 5), isRead: true);

        Assert.Equal("08:05", NoticeViewFilter.FormatTime(item, NoticeListViewMode.Read));
        Assert.Equal("09-06 08:05", NoticeViewFilter.FormatTime(item, NoticeListViewMode.Unread));
    }

    [Fact]
    public void HomeworkFilterToday_KeepsOnlyToday()
    {
        var today = new DateOnly(2026, 9, 6);
        static HomeworkItem Homework(string messageId, DateTimeOffset createdAt) => new()
        {
            MessageId = messageId,
            Content = messageId,
            CreatedAt = createdAt
        };

        var todayMorning = Homework("t1", LocalTime(2026, 9, 6, 7, 30));
        var todayEvening = Homework("t2", LocalTime(2026, 9, 6, 21));
        var yesterday = Homework("y1", LocalTime(2026, 9, 5, 12));

        var result = HomeworkSuspensionWindow.FilterToday([yesterday, todayEvening, todayMorning], today);

        Assert.Equal(2, result.Count);
        // 时间正序
        Assert.Equal(todayMorning, result[0]);
        Assert.Equal(todayEvening, result[1]);
    }

    [Fact]
    public void UtcCreatedAt_LocalEarlyMorning_IsIncludedInToday()
    {
        // CreatedAt 以 UTC 存储：构造「本地今天 07:00」的时间戳（在 UTC+8 下即 UTC 前一天 23:00，
        // 晚于本地当日零点的 UTC 表示），应按本地日期归入今天；UTC 时区下同样成立。
        var today = new DateOnly(2026, 9, 7);
        var utcLastNight = new HomeworkItem
        {
            MessageId = "m1",
            Content = "m1",
            CreatedAt = LocalTime(2026, 9, 7, 7).ToUniversalTime()
        };

        Assert.Single(HomeworkSuspensionWindow.FilterToday([utcLastNight], today));
    }
}
