using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 3 常态化作业的确认浮层行为（窗口级）：
/// <list type="bullet">
/// <item>勾选态必须与当天文档的事实一致——已落档的条目显示为「已勾选且不可取消」，
/// 而不是每次打开都重置为未勾选（旧行为让老师无法判断是否已计入）。</item>
/// <item>预览即实际发送内容：常态化行在清单里只出现一次（落档后重新格式化会再追加一遍，
/// 那是「发送内容里常态化作业重复」的根因）。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StandingHomeworkLandingTests : IDisposable
{
    private const string StandingContent = "校本往后做一课";

    private readonly string _dir;

    public StandingHomeworkLandingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "standing-landing", Guid.NewGuid().ToString("N"));
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

    // ---------- 纯函数：已落档键 ----------

    [Fact]
    public void CollectLandedStandingKeys_OnlyStandingEntriesOfMatchingSubject()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var document = new HomeworkDocument
        {
            Date = today,
            Subject = "数学",
            Entries =
            [
                new HomeworkDocumentEntry
                {
                    Text = StandingContent,
                    SenderLabel = "常态化作业",
                    IsStanding = true,
                    CreatedAt = DateTimeOffset.Now
                },
                new HomeworkDocumentEntry
                {
                    Text = "练习册 P12",
                    MemberOpenId = "t1",
                    CreatedAt = DateTimeOffset.Now
                }
            ]
        };

        var keys = HomeworkSuspensionWindow.CollectLandedStandingKeys([document]);

        Assert.Contains(StandingHomeworkView.LandingKey("数学", StandingContent), keys);
        // 普通作业条目不算落档；学科不同也不算
        Assert.DoesNotContain(StandingHomeworkView.LandingKey("数学", "练习册 P12"), keys);
        Assert.DoesNotContain(StandingHomeworkView.LandingKey("英语", StandingContent), keys);
    }

    // ---------- 窗口级：已落档 / 预览即发送内容 ----------

    [Fact]
    public Task 已落档的常态化作业_显示为已勾选且不可取消_预览不重复()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: true);
            var window = BuildWindow(store, out _);
            try
            {
                window.OpenSendOverlay();

                var view = Assert.Single(window.StandingViews);
                Assert.True(view.IsLanded, "今天已落档的常态化作业应标记为「已落档」");
                Assert.True(view.IsChecked, "已落档条目在清单里本就发出，勾选态应与事实一致");
                Assert.False(view.IsToggleEnabled, "已落档条目不允许取消（取消也无法从文档移除）");
                Assert.Contains("今天已落档", view.DisplayText);

                // 落档条目已在文档里：清单里只能出现一次
                Assert.Equal(1, CountOccurrences(window.SendPreview, StandingContent));
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 未落档的常态化作业_勾选后发送内容与预览一致且只出现一次_并落档()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: false);
            var window = BuildWindow(store, out var send);
            try
            {
                window.OpenSendOverlay();
                var view = Assert.Single(window.StandingViews);
                Assert.False(view.IsLanded);
                Assert.True(view.IsToggleEnabled);

                view.IsChecked = true;
                window.RefreshSendPreview();
                var preview = window.SendPreview;
                Assert.Equal(1, CountOccurrences(preview, StandingContent));
                Assert.Contains(HomeworkDigestFormatter.StandingSuffix, preview);

                var (summary, _) = window.ConfirmSendAsync().GetAwaiter().GetResult();

                Assert.NotNull(send.LastSent);
                Assert.Equal(preview, send.LastSent);                       // 预览即所得
                Assert.Equal(1, CountOccurrences(send.LastSent!, StandingContent)); // 不重复
                Assert.Contains("发送完成", summary);

                // 点「发送」才落档：文档里出现 IsStanding 条目
                var document = store.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now))
                    .GetAwaiter().GetResult().Single();
                var standing = Assert.Single(document.Entries.Where(e => e.IsStanding));
                Assert.Equal(StandingContent, standing.Text);

                // 落档后再打开确认窗：条目变「已落档（已勾选、不可取消）」，且不再重复追加
                window.OpenSendOverlay();
                var reopened = Assert.Single(window.StandingViews);
                Assert.True(reopened.IsLanded);
                Assert.True(reopened.IsChecked);
                Assert.Equal(1, CountOccurrences(window.SendPreview, StandingContent));

                var secondRun = window.ConfirmSendAsync().GetAwaiter().GetResult();
                Assert.NotNull(secondRun);
                Assert.Equal(1, CountOccurrences(send.LastSent!, StandingContent));
                Assert.Single(store.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now))
                    .GetAwaiter().GetResult().Single().Entries.Where(e => e.IsStanding));
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 待发消息可编辑_发送的就是编辑后的文本()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: false);
            var window = BuildWindow(store, out var send);
            try
            {
                window.OpenSendOverlay();
                Assert.Contains("练习册 P12", window.SendPreview);
                Assert.True(window.CanSendPreview());

                // 模拟老师在确认窗里直接改写待发消息
                window.SendPreviewEditorForTest.Text = "数学：练习册 P12（老师手动整理）";
                Assert.True(window.CanSendPreview());

                var (summary, _) = window.ConfirmSendAsync().GetAwaiter().GetResult();

                Assert.Equal("数学：练习册 P12（老师手动整理）", send.LastSent);
                Assert.Contains("发送完成", summary);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 手动编辑后_勾选变更不覆盖待发文本_重新生成可恢复()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: false);
            var window = BuildWindow(store, out _);
            try
            {
                window.OpenSendOverlay();
                window.SendPreviewEditorForTest.Text = "自定义文本";

                // 勾选常态化作业 → 刷新路径不得覆盖老师已改的文本
                var view = Assert.Single(window.StandingViews);
                view.IsChecked = true;
                window.RefreshSendPreview();
                Assert.Equal("自定义文本", window.SendPreview);

                // 点「重新生成清单」→ 回到按当前作业与勾选生成的清单（含常态化行）
                window.RegeneratePreview();
                Assert.Contains("练习册 P12", window.SendPreview);
                Assert.Contains($"{StandingContent}（常态化）", window.SendPreview);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 自动补填序号关闭_清单不带序号_开启时带序号()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: false);
            var window = BuildWindow(store, out _, numberDigestLines: false);
            try
            {
                window.OpenSendOverlay();
                Assert.Contains("【数学】", window.SendPreview);
                Assert.Contains("练习册 P12", window.SendPreview);
                Assert.DoesNotContain("1. ", window.SendPreview);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 自动补填序号开启_清单默认带序号()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: false);
            var window = BuildWindow(store, out _);
            try
            {
                window.OpenSendOverlay();
                Assert.Contains("1. 练习册 P12", window.SendPreview);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 没有作业时_占位提示不可发送_手写内容后可发送()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = new HomeworkStore(_dir); // 当天没有作业
            var window = BuildWindow(store, out var send);
            try
            {
                window.OpenSendOverlay();
                Assert.Equal(HomeworkSuspensionWindow.EmptyDigestText, window.SendPreview);
                Assert.False(window.CanSendPreview()); // 占位提示不可发送

                var (blocked, _) = window.ConfirmSendAsync().GetAwaiter().GetResult();
                Assert.Contains("待发内容为空", blocked);
                Assert.Null(send.LastSent);

                // 老师手写一条 → 可发送，且发送的就是这段
                window.SendPreviewEditorForTest.Text = "今天没有书面作业，请预习第 3 课";
                Assert.True(window.CanSendPreview());
                window.ConfirmSendAsync().GetAwaiter().GetResult();
                Assert.Equal("今天没有书面作业，请预习第 3 课", send.LastSent);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 打开确认窗_启用可编辑模式_关闭后恢复()
    {
        // 回归：无边框悬浮窗默认 WS_EX_NOACTIVATE，收不到键盘消息——
        // 待发消息要能直接打字，打开确认窗必须切到「可编辑」模式，关闭后恢复（与文档编辑态同一机制）
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore(withStandingEntry: false);
            var controller = new RecordingWindowController();
            var window = BuildWindow(store, out _, overlays: controller);
            try
            {
                window.OpenSendOverlay();
                var opened = Assert.Single(controller.EditingCalls);
                Assert.Equal(SuspensionWindowController.HomeworkKey, opened.Key);
                Assert.True(opened.Editing);

                window.HideSendOverlay();
                Assert.Equal(2, controller.EditingCalls.Count);
                Assert.False(controller.EditingCalls[1].Editing);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    // ---------- 测试夹具 ----------
    private HomeworkStore SeedStore(bool withStandingEntry)
    {
        var store = new HomeworkStore(_dir);
        var now = DateTimeOffset.Now;
        store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            Text = "练习册 P12",
            MemberOpenId = "t1",
            SenderLabel = "王老师",
            SourceMessageIds = ["m1"],
            CreatedAt = now
        }).GetAwaiter().GetResult();

        if (withStandingEntry)
        {
            store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
            {
                Text = StandingContent,
                MemberOpenId = "",
                SenderLabel = "常态化作业",
                SourceMessageIds = [],
                CreatedAt = now,
                IsStanding = true
            }).GetAwaiter().GetResult();
        }

        return store;
    }

    private HomeworkSuspensionWindow BuildWindow(
        HomeworkStore store, out CapturingSendService send, bool numberDigestLines = true,
        ISuspensionWindowController? overlays = null)
    {
        EnsureControlTheme();
        var settings = new SettingsService(_dir);
        settings.Current.StandingHomework.Items =
        [
            new StandingHomeworkItem { Subject = "数学", Content = StandingContent, Enabled = true }
        ];
        settings.Current.Connection.TargetGroupOpenIds = ["10001"];
        settings.Current.Connection.NumberDigestLines = numberDigestLines;
        send = new CapturingSendService();
        var window = new HomeworkSuspensionWindow(store, null, settings, send, overlays) { Width = 380, Height = 560 };
        window.Show();
        window.UpdateLayout();
        // 构造里的首轮 RefreshAsync 需要一次 Dispatcher 泵才落到 ItemsSource
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        return window;
    }

    private static void EnsureControlTheme()
    {
        if (Application.Current is { } app && !app.Styles.OfType<SimpleTheme>().Any())
        {
            app.Styles.Add(new SimpleTheme());
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Finish(HomeworkSuspensionWindow window)
    {
        window.Content = null;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>记录「可编辑模式」开关调用的控制器替身（其余成员为占位实现）。</summary>
    private sealed class RecordingWindowController : ISuspensionWindowController
    {
        public List<(string Key, bool Editing)> EditingCalls { get; } = [];

        public Task ShowAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task HideAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetPositionAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task ApplySettingsAsync(string overlayKey, OverlayWindowSettings settings, CancellationToken ct = default)
            => Task.CompletedTask;

        public bool IsOnScreen(string overlayKey) => true;

        public void NotifyHostStopping()
        {
        }

        public void SetOverlayEditing(string overlayKey, bool editing) => EditingCalls.Add((overlayKey, editing));
    }

    private sealed class CapturingSendService : IHomeworkSendService
    {
        public bool IsEnabled => true;

        public string? LastSent { get; private set; }

        public Task<IReadOnlyList<GroupSendResult>> SendTextToTargetGroupsAsync(
            string content, string? msgId = null, CancellationToken ct = default)
        {
            LastSent = content;
            return Task.FromResult<IReadOnlyList<GroupSendResult>>(
            [
                new GroupSendResult("10001", true, null)
            ]);
        }
    }
}
