using Avalonia.Controls;
using Avalonia.LogicalTree;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 悬浮窗「⋯」快捷菜单共享逻辑（OverlayQuickMenu）测试：
/// 勾选态与设置的双向同步、切换回写（控制器应用 + SaveAsync 持久化）、
/// 作业窗学科修正下拉移除后快捷菜单仍在（需求 1/2 回归）、
/// 圆圈栏底部控件条（需求 3 回归）。
/// 控件构造须在 Avalonia UI 线程（Headless 会话调度）。
/// </summary>
public sealed class OverlayQuickMenuTests : IDisposable
{
    private readonly string _dir;

    public OverlayQuickMenuTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "quick-menu", Guid.NewGuid().ToString("N"));
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
    public Task SyncFromAndWriteTo_RoundTripThroughToggles()
    {
        // 纯同步/回写逻辑：设置 → 开关勾选态 → 再写回另一份设置，三开关值一致
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var menu = new OverlayQuickMenu(null, null, "notice", () => new OverlayWindowSettings(), new Window());

            menu.SyncFrom(new OverlayWindowSettings { Topmost = true, Pinned = true, ClickThrough = false });
            var target = new OverlayWindowSettings();
            menu.WriteTo(target);

            Assert.True(target.Topmost);
            Assert.True(target.Pinned);
            Assert.False(target.ClickThrough);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task ToggleWriteBack_AppliesToControllerAndPersists()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var settings = new SettingsService(_dir);
            var controller = new SuspensionWindowController(_dir, settingsService: settings);
            var menu = new OverlayQuickMenu(
                settings, controller, SuspensionWindowController.HomeworkKey,
                () => settings.Current.Overlays.Homework, new Window());

            // 模拟用户展开菜单（SyncFromSettings 读活实例）后切换开关并回写
            menu.SyncFromSettings();
            menu.SyncFrom(new OverlayWindowSettings { Topmost = true, Pinned = true, ClickThrough = true });
            menu.WriteTo(settings.Current.Overlays.Homework);
            await menu.ApplyAsync();

            // 内存即时生效（单一来源活实例）
            Assert.True(settings.Current.Overlays.Homework.Topmost);
            Assert.True(settings.Current.Overlays.Homework.Pinned);
            Assert.True(settings.Current.Overlays.Homework.ClickThrough);

            // 已持久化：新实例读盘可得（经 SaveAsync 落盘，重启后仍可读）
            var reloaded = new SettingsService(_dir);
            Assert.True(reloaded.Current.Overlays.Homework.Topmost);
            Assert.True(reloaded.Current.Overlays.Homework.Pinned);
            Assert.True(reloaded.Current.Overlays.Homework.ClickThrough);
        }, CancellationToken.None);
    }

    [Fact]
    public Task ToggleClick_WithoutControllerOrSettings_IsSafeNoOp()
    {
        // 无控制器/设置服务（设计时等场景）：展开与开关点击不抛异常、不回写
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var menu = new OverlayQuickMenu(null, null, "notice", () => new OverlayWindowSettings(), new Window());

            menu.SyncFromSettings();
            menu.OnToggleClick();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task HomeworkWindow_SubjectFixComboBoxRemoved_QuickMenuStillAttached()
    {
        // 需求 1 回归：作业条目上的「修正学科」下拉已从 UI 移除；
        // 需求 2 回归：右上角「⋯」快捷菜单（Flyout）已挂上
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = new HomeworkSuspensionWindow(new FakeHomeworkStore());
            var descendants = ((Control)window.Content!).GetLogicalDescendants().ToList();

            Assert.DoesNotContain(descendants.OfType<ComboBox>(), _ => true);
            var menuButton = descendants.OfType<Button>().FirstOrDefault(b => b.Name == "QuickMenuButton");
            Assert.NotNull(menuButton);
            Assert.IsType<Flyout>(menuButton!.Flyout);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task CircleBarWindow_CloseAndMenuButtonsShareBottomBar()
    {
        // 需求 3 回归：圆圈栏底部控件条同时含「⋯」（带向上展开的 Flyout）与「×」
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var stub = Path.Combine(Path.GetTempPath(), "classing-tests", "quick-menu-stub");
            Directory.CreateDirectory(stub);
            var window = new SubjectCircleBarWindow(
                new SubjectFilesController(
                    new SuspensionWindowController(stub),
                    new SettingsService(stub),
                    () => null!),
                new FakeCirclePipeline(),
                () => new SubjectCircleBarSettings());
            var descendants = ((Control)window.Content!).GetLogicalDescendants().ToList();

            var menuButton = descendants.OfType<Button>().FirstOrDefault(b => b.Name == "QuickMenuButton");
            var closeButton = descendants.OfType<Button>().FirstOrDefault(b => b.Content as string == "×");
            Assert.NotNull(menuButton);
            Assert.NotNull(closeButton);
            Assert.IsType<Flyout>(menuButton!.Flyout);

            // 两个控件同处底部控件条（同一父容器）
            Assert.Same(menuButton.Parent, closeButton!.Parent);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private sealed class FakeHomeworkStore : IHomeworkStore
    {
        public event EventHandler<HomeworkItem>? Changed
        {
            add { }
            remove { }
        }

        public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
            => Task.FromResult(item);

        public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>([]);

        public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>([]);

        public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HomeworkItem>>([]);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeCirclePipeline : IFilePipelineService
    {
        public event EventHandler<FileRecord>? FileUpdated
        {
            add { }
            remove { }
        }

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
            => throw new NotSupportedException("测试桩不写文件");

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileRecord>>([]);
    }
}
