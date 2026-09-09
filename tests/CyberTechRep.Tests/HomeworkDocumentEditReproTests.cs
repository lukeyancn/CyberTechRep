using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 回归：作业悬浮窗「点击文字进入编辑态」与「未编辑不写手工文本」。
/// <para>
/// 背景：<see cref="SelectableTextBlock"/> 在 Avalonia 11.3 的 <c>OnPointerPressed</c> 里
/// 一律 <c>e.Handled = true</c>（并捕获指针），XAML 事件属性默认 <c>handledEventsToo: false</c>，
/// 所以挂在它身上的 <c>PointerPressed</c> 永远不会触发——点击进入编辑态完全失效。
/// 同时编辑框 Text 的绑定回填会在列表重建时触发 <c>TextChanged</c>，
/// 把「渲染文本」误当作用户编辑写进 ManualText（文档被冻结）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HomeworkDocumentEditReproTests : IDisposable
{
    private const string DocumentText = "练习册 P12 第 1-5 题";

    private readonly string _dir;

    public HomeworkDocumentEditReproTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "hw-edit", Guid.NewGuid().ToString("N"));
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

    private static void EnsureControlTheme()
    {
        if (Application.Current is { } app && !app.Styles.OfType<SimpleTheme>().Any())
        {
            app.Styles.Add(new SimpleTheme());
        }
    }

    private HomeworkStore SeedStore()
    {
        var store = new HomeworkStore(_dir);
        store.AppendDocumentEntryAsync("数学", new HomeworkDocumentEntry
        {
            Text = DocumentText,
            MemberOpenId = "t1",
            SenderLabel = "王老师",
            SourceMessageIds = ["m1"],
            CreatedAt = DateTimeOffset.Now
        }).GetAwaiter().GetResult();
        return store;
    }

    private HomeworkSuspensionWindow LayoutWindow(HomeworkStore store)
    {
        EnsureControlTheme();
        var window = new HomeworkSuspensionWindow(store) { Width = 360, Height = 520 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>排空 Dispatcher 队列并推进挂钟，让 600ms 防抖计时器有机会触发。</summary>
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

    [Fact]
    public Task ClickDocumentText_EntersEditMode_AndFocusesEditor()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore();
            var window = LayoutWindow(store);
            try
            {
                var display = window.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
                var editor = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                Assert.NotNull(display);
                Assert.NotNull(editor);

                var origin = display!.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(origin);
                var point = new Point(
                    origin!.Value.X + (display.Bounds.Width / 2),
                    origin.Value.Y + (display.Bounds.Height / 2));

                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.UpdateLayout();

                var views = window.VisibleDocuments;
                Assert.Single(views);
                Assert.True(views[0].IsEditing, "点击文档文本后未进入编辑态");
                Assert.True(editor!.IsVisible, "编辑框未显示");
                Assert.True(editor.IsFocused, "编辑框未获得焦点");
                Assert.Equal(DocumentText, editor.Text);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task TypingInEditor_PersistsManualText_AndRebuildKeepsUserEdit()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore();
            var window = LayoutWindow(store);
            try
            {
                var display = window.GetVisualDescendants().OfType<SelectableTextBlock>().First();
                var origin = display.TranslatePoint(new Point(0, 0), window)!.Value;
                var point = new Point(origin.X + (display.Bounds.Width / 2), origin.Y + (display.Bounds.Height / 2));

                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.UpdateLayout();

                window.KeyTextInput("（已核对）");
                // 防抖计时器在 Headless 泵下不推进，直接触发落档路径（与计时器回调同一入口）
                window.FlushPendingDocumentSaveAsync().GetAwaiter().GetResult();
                PumpDispatcher(TimeSpan.FromMilliseconds(200));

                var document = store.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now))
                    .GetAwaiter().GetResult().Single();
                Assert.Equal(DocumentText + "（已核对）", document.ManualText);
                Assert.Equal(DocumentText + "（已核对）", document.Render);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task ClickThenLeaveWithoutTyping_DoesNotWriteManualText()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore();
            var window = LayoutWindow(store);
            try
            {
                var display = window.GetVisualDescendants().OfType<SelectableTextBlock>().First();
                var origin = display.TranslatePoint(new Point(0, 0), window)!.Value;
                var point = new Point(origin.X + (display.Bounds.Width / 2), origin.Y + (display.Bounds.Height / 2));

                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.UpdateLayout();
                Assert.True(window.VisibleDocuments[0].IsEditing, "点击文档文本后未进入编辑态");

                // 只点进编辑态、没输入任何字符：落档路径必须直接跳过（否则渲染文本被冻结成手工文本）
                window.FlushPendingDocumentSaveAsync().GetAwaiter().GetResult();

                var document = store.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now))
                    .GetAwaiter().GetResult().Single();
                Assert.Null(document.ManualText);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("a", "a", false)]
    [InlineData("a", "ab", true)]
    [InlineData("ab", "a", true)]
    [InlineData("", "a", true)]
    [InlineData("a", null, true)]
    public void HasUserChanges_ComparesEditorTextWithBaseline(string? editorText, string? baseline, bool expected)
        => Assert.Equal(expected, HomeworkSuspensionWindow.HasUserChanges(editorText, baseline));

    [Fact]
    public Task ListRebuild_WithoutUserEdit_DoesNotWriteManualText()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var store = SeedStore();
            var window = LayoutWindow(store);
            try
            {
                // 列表重建（首次布局即会经绑定回填编辑框 Text）后静置，
                // 未发生任何用户输入：存档 ManualText 必须保持为 null（文档按条目渲染）
                PumpDispatcher(TimeSpan.FromSeconds(1.5));

                var document = store.GetDocumentsAsync(DateOnly.FromDateTime(DateTime.Now))
                    .GetAwaiter().GetResult().Single();
                Assert.Null(document.ManualText);
                Assert.Equal(DocumentText, document.Render);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }
}
