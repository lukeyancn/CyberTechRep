using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Views;

/// <summary>通知悬浮窗行视图（条目 + 操作按钮文案 + 时间展示）。</summary>
public sealed class NoticeRow
{
    public required NoticeItem Item { get; init; }

    /// <summary>条目右侧按钮文案：未读视图=「已读」，已读视图=「标记未读」。</summary>
    public string ButtonText { get; init; } = "已读";

    /// <summary>时间展示：未读视图跨天显示 MM-dd HH:mm；已读视图仅当天，显示 HH:mm。</summary>
    public string TimeText { get; init; } = "";
}

/// <summary>通知悬浮窗列表视图模式（会话内记忆，不持久化）。</summary>
internal enum NoticeListViewMode
{
    Unread,

    Read
}

/// <summary>通知悬浮窗列表纯逻辑（可脱离 UI 单测）。</summary>
internal static class NoticeViewFilter
{
    /// <summary>
    /// 已读视图数据：仅显示<b>当天</b>（本地日期）已读条目（时间倒序）。
    /// 未读视图与此不同：全部未读全部保留、不限时间（调用方直接取 GetUnreadAsync）。
    /// </summary>
    public static IReadOnlyList<NoticeItem> SelectReadToday(IEnumerable<NoticeItem> items, DateOnly today)
    {
        return items
            .Where(i => i.IsRead && DateOnly.FromDateTime(i.CreatedAt.LocalDateTime) == today)
            .OrderByDescending(i => i.CreatedAt)
            .ToList();
    }

    /// <summary>行时间文案：已读视图仅当天 → HH:mm；未读视图跨天 → MM-dd HH:mm。</summary>
    public static string FormatTime(NoticeItem item, NoticeListViewMode mode)
    {
        var local = item.CreatedAt.LocalDateTime;
        return mode == NoticeListViewMode.Read ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
    }
}

/// <summary>
/// 通知悬浮窗（Avalonia 无边框置顶窗）：
/// 左下角「未读/已读」视图切换（默认未读：所有未读全部保留、不限时间；已读：仅当天已读，
/// 条目按钮变为「标记未读」，点击经 NoticeStore 写回并持久化，条目回到未读）；
/// 右上角「⋯」快捷菜单（Flyout）：置顶/固定/鼠标穿透开关与设置页等价，切换即时生效并回写
/// ISettingsService（经控制器的 ApplySettingsAsync 路径应用），设置页修改后经
/// SettingsChanged 双向同步勾选态；Store.Changed 触发 200ms debounce 合并刷新；
/// 标题栏 BeginMoveDrag 拖拽、角部 Thumb 缩放；位置/大小由 <see cref="SuspensionWindowController"/> 持久化。
/// </summary>
public partial class NoticeSuspensionWindow : Window, IOverlayInteractiveRegionProvider, IOverlayContentFontSizeAware
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（限流，避免刷屏）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly INoticeStore _store = null!;
    private readonly IMessageReclassifyService? _reclassify;
    private readonly DispatcherTimer _debounceTimer = null!;
    private readonly OverlayQuickMenu _quickMenu = null!;

    /// <summary>需求 6：右键菜单展开时记录的只读内容文本框（MenuItem 不在可视树内）。</summary>
    private TextBox? _contextMenuTextBox;
    private int _refreshing;
    private NoticeListViewMode _viewMode = NoticeListViewMode.Unread;

    /// <summary>可交互区域缓存（需求 6；穿透模式下 WM_NCHITTEST 高频读取，只做返回不做计算）。</summary>
    private Rect _interactiveRegion;

    public NoticeSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含依赖构造
        InitializeComponent();
    }

    public NoticeSuspensionWindow(
        INoticeStore store,
        ISuspensionWindowController? overlays = null,
        ISettingsService? settingsService = null,
        IMessageReclassifyService? reclassify = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reclassify = reclassify;
        InitializeComponent();
        _debounceTimer = new DispatcherTimer { Interval = RefreshDebounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RefreshAsync();
        };
        _store.Changed += OnStoreChanged;

        // 需求 6：维护「通知内容可交互区域」（滚动/布局变化后重算；穿透模式下命中测试据此分流）
        LayoutUpdated += (_, _) => UpdateInteractiveRegion();
        NoticeScroll.ScrollChanged += (_, _) => UpdateInteractiveRegion();
        SizeChanged += (_, _) => UpdateInteractiveRegion();

        // 右上角「⋯」快捷菜单：置顶/固定/鼠标穿透开关与设置页等价（共享 OverlayQuickMenu），
        // 切换即时生效并回写 ISettingsService（经控制器的 ApplySettingsAsync 路径应用），
        // 设置页修改后经 SettingsChanged 双向同步勾选态
        _quickMenu = new OverlayQuickMenu(
            settingsService, overlays, SuspensionWindowController.NoticeKey,
            () => settingsService!.Current.Overlays.Notice, this);
        QuickMenuButton.Flyout = _quickMenu.Flyout;

        _ = RefreshAsync();
    }

    /// <summary>
    /// 需求 6：鼠标穿透开启时保留交互的区域 = 所有通知内容只读文本框的并集，裁剪到滚动视口内。
    /// 窗口客户区逻辑坐标（DIP）；空矩形 = 整窗穿透（列表为空/未布局时）。
    /// </summary>
    public Rect GetInteractiveRegion() => _interactiveRegion;

    /// <summary>
    /// 正文（可选中复制的只读文本）字号跟随悬浮窗设置「字号」。
    /// <para>
    /// 走窗口资源 + 样式动态资源：宿主主题（Fluent）给 TextBox 的 ControlTheme 自带 FontSize，
    /// 优先级高于窗口级属性继承——只在窗口上设字号时正文不会变（用户实测缺陷）。
    /// 样式优先级高于 ControlTheme，资源改值后已生成与后续滚动生成的行都即时生效。
    /// </para>
    /// </summary>
    public void ApplyContentFontSize(double fontSize) =>
        Resources["CyberTechRepOverlayContentFontSize"] = fontSize;

    /// <summary>重算可交互区域（UI 线程；布局/滚动/刷新后调用）。异常一律吞掉，退回整窗穿透。</summary>
    private void UpdateInteractiveRegion()
    {
        try
        {
            var viewport = GetViewportInWindow();
            if (viewport is not { Width: > 0, Height: > 0 } clip)
            {
                _interactiveRegion = default;
                return;
            }

            var hasRegion = false;
            double left = 0, top = 0, right = 0, bottom = 0;
            foreach (var textBox in NoticeList.GetVisualDescendants().OfType<TextBox>())
            {
                if (!textBox.IsVisible || textBox.Bounds.Width <= 0 || textBox.Bounds.Height <= 0)
                {
                    continue;
                }

                var origin = textBox.TranslatePoint(new Point(0, 0), this);
                if (origin is null)
                {
                    continue;
                }

                // 裁剪到滚动视口（滚出视口的行应保持穿透），再并入并集
                var x1 = Math.Max(origin.Value.X, clip.X);
                var y1 = Math.Max(origin.Value.Y, clip.Y);
                var x2 = Math.Min(origin.Value.X + textBox.Bounds.Width, clip.Right);
                var y2 = Math.Min(origin.Value.Y + textBox.Bounds.Height, clip.Bottom);
                if (x2 <= x1 || y2 <= y1)
                {
                    continue;
                }

                if (!hasRegion)
                {
                    left = x1;
                    top = y1;
                    right = x2;
                    bottom = y2;
                    hasRegion = true;
                }
                else
                {
                    left = Math.Min(left, x1);
                    top = Math.Min(top, y1);
                    right = Math.Max(right, x2);
                    bottom = Math.Max(bottom, y2);
                }
            }

            _interactiveRegion = hasRegion
                ? new Rect(left, top, right - left, bottom - top)
                : default;
        }
        catch
        {
            _interactiveRegion = default; // 兜底：整窗穿透（不因 UI 异常导致「点不透」）
        }
    }

    /// <summary>滚动视口在窗口客户区中的矩形（列表区域）；未布局完成返回 null。</summary>
    private Rect? GetViewportInWindow()
    {
        var origin = NoticeScroll.TranslatePoint(new Point(0, 0), this);
        return origin is null ? null : new Rect(origin.Value, NoticeScroll.Bounds.Size);
    }

    /// <summary>当前视图模式（测试用）。</summary>
    internal NoticeListViewMode ViewMode => _viewMode;

    /// <summary>当前列表数据源（测试用）。</summary>
    public System.Collections.IEnumerable? VisibleItems => NoticeList?.ItemsSource;

    private void OnStoreChanged(object? sender, NoticeItem e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ScheduleRefresh();
        }
        else
        {
            Dispatcher.UIThread.Post(ScheduleRefresh);
        }
    }

    /// <summary>UI 侧限流：Changed 高频触发时合并为 200ms 一次刷新。</summary>
    private void ScheduleRefresh()
    {
        // 重启计时器实现 debounce（合并密集事件）
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    internal async Task RefreshAsync()
    {
        // 防并发重入（Refresh 本身在 UI 线程，存储查询同步完成，此为兜底）
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            IReadOnlyList<NoticeItem> items;
            if (_viewMode == NoticeListViewMode.Unread)
            {
                // 未读视图：所有未读全部保留，不限时间
                items = await _store.GetUnreadAsync();
            }
            else
            {
                // 已读视图：复用存储的按日期桶查询（GetByDateAsync），仅显示当天已读
                var today = DateOnly.FromDateTime(DateTime.Now);
                items = NoticeViewFilter.SelectReadToday(await _store.GetByDateAsync(today), today);
            }

            var rows = items
                .Select(i => new NoticeRow
                {
                    Item = i,
                    ButtonText = _viewMode == NoticeListViewMode.Unread ? "已读" : "标记未读",
                    TimeText = NoticeViewFilter.FormatTime(i, _viewMode)
                })
                .ToList();

            EmptyText.Text = _viewMode == NoticeListViewMode.Unread ? "暂无未读通知" : "今天没有已读通知";
            EmptyText.IsVisible = rows.Count == 0;
            NoticeList.ItemsSource = rows;
            UpdateInteractiveRegion();
        }
        catch
        {
            // 刷新失败保持上次内容，不阻断悬浮窗
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>视图切换（会话内记忆）：未读 → 已读时列表换数据源并刷新。</summary>
    private void OnUnreadViewChecked(object? sender, RoutedEventArgs e) => SetViewMode(NoticeListViewMode.Unread);

    private void OnReadViewChecked(object? sender, RoutedEventArgs e) => SetViewMode(NoticeListViewMode.Read);

    private void SetViewMode(NoticeListViewMode mode)
    {
        if (_viewMode == mode)
        {
            return;
        }

        _viewMode = mode;
        _ = RefreshAsync();
    }

    /// <summary>右键菜单展开时记录所属的只读文本框（MenuItem 不在可视树内，经 ContextMenu 的 PlacementTarget 取）。</summary>
    private void OnNoticeContentMenuOpened(object? sender, RoutedEventArgs e)
    {
        _contextMenuTextBox = sender is ContextMenu { PlacementTarget: TextBox box } ? box : null;
    }

    /// <summary>
    /// 需求 6：复制通知内容。悬浮窗带 WS_EX_NOACTIVATE（不抢焦点），键盘 Ctrl+C 可能收不到，
    /// 因此显式提供右键「复制」——有选中文本复制选中部分，无选中复制全文。
    /// </summary>
    private async void OnCopyNoticeContentClick(object? sender, RoutedEventArgs e)
    {
        var box = _contextMenuTextBox;
        if (box is null)
        {
            return;
        }

        var text = box.SelectedText;
        if (string.IsNullOrEmpty(text))
        {
            text = box.Text;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(box)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // 剪贴板被其他进程占用等：复制失败不影响悬浮窗（用户可重试）
        }
    }

    /// <summary>需求 6：全选通知内容（配合右键「复制」在无键盘焦点时也能整段复制）。</summary>
    private void OnSelectAllNoticeContentClick(object? sender, RoutedEventArgs e)
    {
        _contextMenuTextBox?.SelectAll();
    }

    private async void OnRowActionButtonClick(object? sender, RoutedEventArgs e)
    {        try
        {
            if (sender is not Button { DataContext: NoticeRow row })
            {
                return;
            }

            if (_viewMode == NoticeListViewMode.Unread)
            {
                await _store.MarkReadAsync(row.Item.Id);
                // 条目消失经 Changed → debounce 刷新完成
            }
            else
            {
                // 已读视图「标记未读」：回未读并保持持久化语义（IsRead/ReadAt 写回 notices.json）
                await _store.MarkUnreadAsync(row.Item.Id);
            }
        }
        catch
        {
            // 标记失败保持条目，不中断
        }
    }

    // ---- 需求 5：通知 → 作业一键换类 ----

    /// <summary>「转为作业」候选学科（固定七学科 + 未分类；本条已有通知学科时置顶）。</summary>
    internal static IReadOnlyList<string> BuildReclassifySubjectCandidates(string? noticeSubject)
    {
        var candidates = new List<string>();
        var subject = noticeSubject?.Trim() ?? "";
        if (subject.Length > 0 && !HomeworkSuspensionWindow.BaseSubjects.Contains(subject, StringComparer.Ordinal))
        {
            candidates.Add(subject);
        }

        candidates.AddRange(HomeworkSuspensionWindow.BaseSubjects);
        if (!candidates.Contains("未分类", StringComparer.Ordinal))
        {
            candidates.Add("未分类");
        }

        return candidates;
    }

    /// <summary>
    /// 「转为作业」：弹出学科菜单，选择后把该通知连同内容/来源/时间元信息迁入作业存档
    /// （写入消息类型覆盖记录，重启后保持且重投不回摆）。
    /// </summary>
    private void OnNoticeToHomeworkClick(object? sender, RoutedEventArgs e)
    {
        if (_reclassify is null || sender is not Control { DataContext: NoticeRow row } target)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var subject in BuildReclassifySubjectCandidates(row.Item.Subject))
        {
            var item = new MenuItem { Header = subject };
            var chosen = subject;
            item.Click += async (_, _) =>
            {
                try
                {
                    await _reclassify.MoveNoticeToHomeworkAsync(row.Item.Id, chosen);
                    // 通知存档移除 + 作业文档写入各自触发 Changed → 两窗 debounce 刷新
                }
                catch
                {
                    // 迁移失败保持现状，不中断悬浮窗
                }
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(target);
    }

    // ---- 右上角快捷菜单（与设置页等价的开关，即时生效 + 回写 ISettingsService）----
    // 构建与同步/回写逻辑统一在共享的 OverlayQuickMenu（与作业/学科文件/圆圈栏同款），
    // 通知窗此处仅负责把 Flyout 挂到「⋯」按钮上。

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 固定模式下禁用拖拽（位置只能经设置页调整）
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHideClick(object? sender, RoutedEventArgs e) => Hide();

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        // 固定模式下禁用缩放
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        Width = Math.Max(MinWidth, Width + e.Vector.X);
        Height = Math.Max(MinHeight, Height + e.Vector.Y);
    }
}
