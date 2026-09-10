using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.MessageAccess;
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

    private HomeworkSuspensionWindow BuildWindow(HomeworkStore store, out CapturingSendService send)
    {
        EnsureControlTheme();
        var settings = new SettingsService(_dir);
        settings.Current.StandingHomework.Items =
        [
            new StandingHomeworkItem { Subject = "数学", Content = StandingContent, Enabled = true }
        ];
        settings.Current.Connection.TargetGroupOpenIds = ["10001"];
        send = new CapturingSendService();
        var window = new HomeworkSuspensionWindow(store, null, settings, send) { Width = 380, Height = 560 };
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
