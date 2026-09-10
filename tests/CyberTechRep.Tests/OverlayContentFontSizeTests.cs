using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 悬浮窗「字号」必须真正作用到正文（可选中复制的通知内容、可编辑的作业文档）。
/// <para>
/// 回归背景：只在窗口上设 FontSize 时，宿主主题（Fluent）给 TextBox/SelectableTextBlock 的
/// ControlTheme 自带 FontSize，优先级高于窗口级属性继承——正文始终按主题字号渲染，
/// 用户改字号看不到任何变化（实测缺陷）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayContentFontSizeTests : IDisposable
{
    private readonly string _dir;

    public OverlayContentFontSizeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "overlay-font", Guid.NewGuid().ToString("N"));
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
    public Task 通知窗正文字号_跟随悬浮窗字号()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            EnsureControlTheme();
            var notices = new NoticeStore(_dir);
            notices.AddOrUpdateAsync("m1", "通知内容测试文本").GetAwaiter().GetResult();
            var window = new NoticeSuspensionWindow(notices) { Width = 320, Height = 420 };
            window.Show();
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            try
            {
                ((IOverlayContentFontSizeAware)window).ApplyContentFontSize(22);
                window.UpdateLayout();
                PumpDispatcher(TimeSpan.FromMilliseconds(150));

                var content = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Classes.Contains("notice-content"));
                Assert.Equal(22, content.FontSize);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 作业窗文档正文与编辑框字号_跟随悬浮窗字号()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            EnsureControlTheme();
            var store = new HomeworkStore(_dir);
            store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
            {
                Text = "练习册 P12",
                MemberOpenId = "t1",
                SourceMessageIds = ["m1"],
                CreatedAt = DateTimeOffset.Now
            }).GetAwaiter().GetResult();
            var window = new HomeworkSuspensionWindow(store) { Width = 360, Height = 520 };
            window.Show();
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            try
            {
                ((IOverlayContentFontSizeAware)window).ApplyContentFontSize(22);
                window.UpdateLayout();
                PumpDispatcher(TimeSpan.FromMilliseconds(150));

                var display = window.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Single(box => box.Classes.Contains("document-content"));
                var editor = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Classes.Contains("document-content"));
                Assert.Equal(22, display.FontSize);
                Assert.Equal(22, editor.FontSize);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task 控制器应用设置_把字号转交给窗口正文()
    {
        // 接线回归：ApplyToWindow 必须调用 IOverlayContentFontSizeAware（否则窗口正文不会跟着设置变）
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var settings = new SettingsService(_dir);
            var controller = new SuspensionWindowController(_dir, settingsService: settings);
            var probe = new FontSizeProbeWindow();
            try
            {
                controller.ApplyToWindow(
                    SuspensionWindowController.NoticeKey,
                    probe,
                    new OverlayWindowSettings { FontSize = 22, Width = 300, Height = 200 });

                Assert.Equal(22, probe.AppliedContentFontSize);
                Assert.Equal(22, probe.FontSize);
            }
            finally
            {
                probe.Content = null;
                probe.Close();
            }
        }, CancellationToken.None);
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

    private static void Finish(Window window)
    {
        window.Content = null;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>测试替身：只记录控制器转交过来的内容字号。</summary>
    private sealed class FontSizeProbeWindow : Window, IOverlayContentFontSizeAware
    {
        public double? AppliedContentFontSize { get; private set; }

        public void ApplyContentFontSize(double fontSize) => AppliedContentFontSize = fontSize;
    }
}
