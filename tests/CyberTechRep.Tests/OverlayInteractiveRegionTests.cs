using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 6 端到端验证：通知悬浮窗「内容可选不可编辑」+ 穿透模式下的可交互区域分流。
/// <para>
/// 这里用真实 <see cref="NoticeSuspensionWindow"/> 实例（Avalonia Headless 真实布局）验证
/// 可交互区域确实覆盖内容文本框、且不覆盖标题栏与行内按钮；命中测试分流本身是纯逻辑，
/// 由 <see cref="OverlayHitTestRouterTests"/> 覆盖（Win32 消息循环无法在无显示设备环境驱动）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayInteractiveRegionTests
{
    /// <summary>最小通知存储替身：只提供本测试需要的读取路径。</summary>
    private sealed class FakeNoticeStore : INoticeStore
    {
        public List<NoticeItem> Items { get; } = [];

#pragma warning disable CS0067 // 测试不触发变更事件
        public event EventHandler<NoticeItem>? Changed;
#pragma warning restore CS0067

        public Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null,
            string? groupOpenId = null, DateTimeOffset? createdAt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task MarkReadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkUnreadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

        public Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

        public Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoticeItem>>([.. Items]);

        public Task<int> CleanupAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> RemoveByMessageIdAsync(string messageId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static NoticeItem Notice(string messageId, string content) => new()
    {
        MessageId = messageId,
        Content = content,
        CreatedAt = DateTimeOffset.Now
    };

    /// <summary>
    /// 构造并完成一次真实布局的通知悬浮窗（Headless 真实测量/排列；测试程序集经
    /// <see cref="TestAppBuilder"/> 启用了 Skia 渲染后端，系统字体可用于文本测量）。
    /// 收尾必须调用 <see cref="Finish"/> 排空挂起的渲染任务。
    /// </summary>
    private static NoticeSuspensionWindow LayoutNoticeWindow(params NoticeItem[] notices)
    {
        EnsureControlTheme();
        var store = new FakeNoticeStore();
        store.Items.AddRange(notices);
        var window = new NoticeSuspensionWindow(store) { Width = 320, Height = 480 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>
    /// 给当前测试应用实例挂上控件主题（SimpleTheme，包已随测试工程引用）。
    /// <para>
    /// Headless 会话默认是「空应用」，不带任何控件主题；没有主题就没有 ItemsPresenter，
    /// <c>ItemsControl</c> 不会生成条目容器，通知内容文本框也就不会出现在可视树里。
    /// 只在测试内挂载（会话按测试隔离应用实例），不影响其他测试的会话环境。
    /// </para>
    /// </summary>
    private static void EnsureControlTheme()
    {
        if (Application.Current is { } app && !app.Styles.OfType<SimpleTheme>().Any())
        {
            app.Styles.Add(new SimpleTheme());
        }
    }

    /// <summary>
    /// 测试收尾：断开可视树并在会话仍存活时排空挂起的渲染任务。
    /// <para>
    /// Headless 会话按测试隔离应用实例，销毁时会执行遗留的 Dispatcher 渲染任务；此时字体
    /// 管理器已释放，渲染带文本的控件会抛 <c>fonts:SystemFonts</c> 缺失（测试宿主副作用，
    /// 与被测代码无关）。故在测试体内先断树、再 RunJobs 排空，保证失败只来自断言。
    /// </para>
    /// </summary>
    private static void Finish(NoticeSuspensionWindow window)
    {
        window.Content = null;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<TextBox> ContentBoxes(Window window) => window
        .GetVisualDescendants()
        .OfType<TextBox>()
        .Where(t => t.Classes.Contains("notice-content"))
        .ToList();

    [Fact]
    public Task InteractiveRegion_CoversContentText_ExcludesHeaderAndRowButtons()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = LayoutNoticeWindow(
                Notice("m1", "语文作业：抄写第 3 课生字，明天默写"),
                Notice("m2", "数学作业：练习册 P12 全部"));
            try
            {
                var boxes = ContentBoxes(window);
                Assert.Equal(2, boxes.Count);

                var region = window.GetInteractiveRegion();

                // 可交互区域非空：穿透开启时内容区仍接收鼠标
                Assert.True(region.Width > 0 && region.Height > 0, $"可交互区域为空：{region}");

                // 每个内容文本框都完整落在区域内（区域为并集外接矩形，行间空隙一并包含）
                foreach (var box in boxes)
                {
                    var origin = box.TranslatePoint(new Point(0, 0), window);
                    Assert.NotNull(origin);
                    var boxRect = new Rect(origin!.Value, box.Bounds.Size);
                    Assert.True(
                        region.Contains(boxRect.TopLeft + new Point(0.5, 0.5)),
                        $"内容文本框左上角不在可交互区域内：框={boxRect} 区域={region}");
                    Assert.True(
                        region.Contains(new Point(boxRect.Right - 0.5, boxRect.Bottom - 0.5)),
                        $"内容文本框右下角不在可交互区域内：框={boxRect} 区域={region}");
                }

                // 标题栏（含「⋯」「×」按钮）保持穿透：区域不得从窗口顶部开始
                Assert.True(region.Top > 0, $"可交互区域覆盖到了标题栏：{region}");

                var quickMenu = window.FindControl<Button>("QuickMenuButton");
                Assert.NotNull(quickMenu);
                var menuOrigin = quickMenu!.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(menuOrigin);
                var menuCenter = new Rect(menuOrigin!.Value, quickMenu.Bounds.Size).Center;
                Assert.False(region.Contains(menuCenter), "标题栏按钮落在了可交互区域内（应为穿透）");

                // 行内「已读 / 转为作业」按钮列同样保持穿透：区域右边界不到窗口右沿
                Assert.True(region.Right < window.Bounds.Width, $"可交互区域覆盖到了行内按钮列：{region}");
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task InteractiveRegion_Empty_WhenNoNotices()
    {
        // 列表为空：无可交互区域 → 整窗穿透（不因「空矩形」误判为可交互）
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = LayoutNoticeWindow();
            try
            {
                Assert.Empty(ContentBoxes(window));

                var region = window.GetInteractiveRegion();
                Assert.True(region.Width <= 0 || region.Height <= 0, $"空列表不应有可交互区域：{region}");
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task NoticeContent_IsReadOnly_YetSelectable_WithVisibleSelectionAndCopyMenu()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = LayoutNoticeWindow(Notice("m1", "语文作业：抄写第 3 课生字"));
            try
            {
                var box = Assert.Single(ContentBoxes(window));

                // 不可编辑：只读 + 不参与 Tab 焦点（悬浮窗 WS_EX_NOACTIVATE 下不抢焦点）
                Assert.True(box.IsReadOnly);
                Assert.False(box.IsTabStop);
                Assert.True(box.AcceptsReturn);
                Assert.Equal(TextWrapping.Wrap, box.TextWrapping);

                // 选中高亮可见（显式 SelectionBrush，半透明色也要求 alpha > 0）
                var selectionBrush = Assert.IsAssignableFrom<ISolidColorBrush>(box.SelectionBrush);
                Assert.True(selectionBrush.Color.A > 0, "SelectionBrush 全透明会导致选中不可见");

                // 可选中：全选后选中文本等于内容全文（鼠标拖选走同一条选中路径）
                box.SelectAll();
                Assert.Equal(box.Text, box.SelectedText);

                // 不可编辑：聚焦后模拟键盘输入，内容不变（IsReadOnly 挡的是用户输入）
                var before = box.Text;
                box.Focus();
                window.KeyTextInput("篡改");
                Assert.Equal(before, box.Text);

                // WS_EX_NOACTIVATE 下键盘 Ctrl+C 可能收不到：右键「复制 / 全选」兜底必须存在
                var menu = Assert.IsType<ContextMenu>(box.ContextMenu);
                var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
                Assert.Contains("复制", headers);
                Assert.Contains("全选", headers);
            }
            finally
            {
                Finish(window);
            }
        }, CancellationToken.None);
    }
}

/// <summary>
/// 需求 6 命中测试分流纯逻辑单测：区域内接收鼠标、区域外穿透、空区域整窗穿透，
/// 以及 DPI 换算与 <c>WM_NCHITTEST</c> 坐标拆包（多显示器负坐标）。
/// </summary>
public sealed class OverlayHitTestRouterTests
{
    private static readonly Rect Region = new(10, 40, 200, 300);

    /// <summary>按 Win32 规则打包屏幕坐标：低 16 位 X、高 16 位 Y。</summary>
    private static IntPtr Pack(int x, int y)
        => new(unchecked((uint)(ushort)(short)x | ((uint)(ushort)(short)y << 16)));

    [Fact]
    public void IsInteractive_InsideRegion_ReceivesMouse()
    {
        Assert.True(OverlayHitTestRouter.IsInteractive(Region, new Point(100, 200)));
        Assert.True(OverlayHitTestRouter.IsInteractive(Region, Region.TopLeft + new Point(0.5, 0.5)));
    }

    [Fact]
    public void IsInteractive_OutsideRegion_PassesThrough()
    {
        Assert.False(OverlayHitTestRouter.IsInteractive(Region, new Point(5, 200)));   // 左侧外
        Assert.False(OverlayHitTestRouter.IsInteractive(Region, new Point(100, 30)));  // 标题栏方向
        Assert.False(OverlayHitTestRouter.IsInteractive(Region, new Point(300, 200))); // 右侧按钮列方向
        Assert.False(OverlayHitTestRouter.IsInteractive(Region, new Point(100, 400))); // 下方底栏方向
    }

    [Fact]
    public void IsInteractive_EmptyRegion_IsAlwaysTransparent()
    {
        // 列表为空/未布局：空矩形 = 无可交互区域 → 整窗穿透
        Assert.False(OverlayHitTestRouter.IsInteractive(default, new Point(0, 0)));
        Assert.False(OverlayHitTestRouter.IsInteractive(new Rect(10, 40, 0, 300), new Point(10, 40)));
        Assert.False(OverlayHitTestRouter.IsInteractive(new Rect(10, 40, 200, 0), new Point(10, 40)));
        Assert.False(OverlayHitTestRouter.IsInteractive(new Rect(10, 40, -5, -5), new Point(10, 40)));
    }

    [Fact]
    public void ToClientLogical_ConvertsPixelsToDip()
    {
        Assert.Equal(new Point(200, 100), OverlayHitTestRouter.ToClientLogical(400, 200, 2.0));
        Assert.Equal(new Point(400, 200), OverlayHitTestRouter.ToClientLogical(400, 200, 1.0));
    }

    [Fact]
    public void ToClientLogical_InvalidScaling_FallsBackToIdentity()
    {
        // 句柄失效/窗口未布局时 RenderScaling 可能为 0：按 1.0 兜底，绝不除零
        Assert.Equal(new Point(400, 200), OverlayHitTestRouter.ToClientLogical(400, 200, 0));
        Assert.Equal(new Point(400, 200), OverlayHitTestRouter.ToClientLogical(400, 200, -1));
    }

    [Fact]
    public void UnpackScreenPoint_RoundTripsPositiveCoordinates()
    {
        var (x, y) = OverlayHitTestRouter.UnpackScreenPoint(Pack(1920, 1080));
        Assert.Equal(1920, x);
        Assert.Equal(1080, y);
    }

    [Fact]
    public void UnpackScreenPoint_HandlesNegativeCoordinates_ForLeftTopMonitors()
    {
        // 多显示器：主屏左侧/上方的副屏坐标为负（short 截断必须保留符号）
        var (x, y) = OverlayHitTestRouter.UnpackScreenPoint(Pack(-1920, -300));
        Assert.Equal(-1920, x);
        Assert.Equal(-300, y);
    }

    [Fact]
    public void HitTestRouting_ComposesUnpackScaleAndRegionCheck()
    {
        // 端到端纯逻辑链路：屏幕像素 (420, 240) → ScreenToClient 后客户区像素 (400, 200)
        // → 缩放 2.0 → 逻辑 (200, 100) → 落在区域内 → 接收鼠标
        var (screenX, screenY) = OverlayHitTestRouter.UnpackScreenPoint(Pack(420, 240));
        var logical = OverlayHitTestRouter.ToClientLogical(screenX - 20, screenY - 40, 2.0);

        Assert.True(OverlayHitTestRouter.IsInteractive(Region, logical));
    }
}
