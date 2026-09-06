using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIng.Plugin.Services.Overlays;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Views;

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
public partial class NoticeSuspensionWindow : Window
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（限流，避免刷屏）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly INoticeStore _store = null!;
    private readonly ISuspensionWindowController? _overlays;
    private readonly ISettingsService? _settingsService;
    private readonly DispatcherTimer _debounceTimer = null!;
    private EventHandler<ClassIng.Shared.Models.AppSettings>? _settingsChangedHandler;
    private int _refreshing;
    private NoticeListViewMode _viewMode = NoticeListViewMode.Unread;
    private bool _syncingQuickMenu;

    public NoticeSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含依赖构造
        InitializeComponent();
    }

    public NoticeSuspensionWindow(
        INoticeStore store,
        ISuspensionWindowController? overlays = null,
        ISettingsService? settingsService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _overlays = overlays;
        _settingsService = settingsService;
        InitializeComponent();
        _debounceTimer = new DispatcherTimer { Interval = RefreshDebounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RefreshAsync();
        };
        _store.Changed += OnStoreChanged;

        // 设置页修改悬浮窗开关后双向同步快捷菜单勾选态（经 SettingsChanged；控制器回写也走该广播）
        if (_settingsService is not null)
        {
            _settingsChangedHandler = (_, _) => Dispatcher.UIThread.Post(SyncQuickMenuFromSettings);
            _settingsService.SettingsChanged += _settingsChangedHandler;
            Closed += (_, _) => _settingsService.SettingsChanged -= _settingsChangedHandler;
        }

        _ = RefreshAsync();
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

    private async void OnRowActionButtonClick(object? sender, RoutedEventArgs e)
    {
        try
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

    // ---- 右上角快捷菜单（与设置页等价的开关，即时生效 + 回写 ISettingsService）----

    private void OnQuickMenuOpened(object? sender, EventArgs e) => SyncQuickMenuFromSettings();

    /// <summary>快捷菜单勾选态 ← 设置（设置页修改后经 SettingsChanged 到达，双向同步）。</summary>
    private void SyncQuickMenuFromSettings()
    {
        if (_settingsService is null)
        {
            return;
        }

        var notice = _settingsService.Current.Overlays.Notice;
        _syncingQuickMenu = true;
        try
        {
            TopmostToggle.IsChecked = notice.Topmost;
            PinnedToggle.IsChecked = notice.Pinned;
            ClickThroughToggle.IsChecked = notice.ClickThrough;
        }
        finally
        {
            _syncingQuickMenu = false;
        }
    }

    private void OnQuickToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_syncingQuickMenu || _settingsService is null || _overlays is null)
        {
            return;
        }

        // 回写单一来源（ISettingsService.Current.Overlays.Notice），再经控制器 ApplySettingsAsync
        // 路径即时应用到窗口，最后 SaveAsync 持久化并广播（设置页勾选态随 SettingsChanged 刷新）。
        var notice = _settingsService.Current.Overlays.Notice;
        notice.Topmost = TopmostToggle.IsChecked == true;
        notice.Pinned = PinnedToggle.IsChecked == true;
        notice.ClickThrough = ClickThroughToggle.IsChecked == true;
        _ = ApplyQuickSettingsAsync();
    }

    private async Task ApplyQuickSettingsAsync()
    {
        try
        {
            await _overlays!.ApplySettingsAsync(
                SuspensionWindowController.NoticeKey, _settingsService!.Current.Overlays.Notice);
            await _settingsService!.SaveAsync();
        }
        catch
        {
            // 快捷菜单应用失败不中断悬浮窗（内存中状态保留，设置页可再改）
        }
    }

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
