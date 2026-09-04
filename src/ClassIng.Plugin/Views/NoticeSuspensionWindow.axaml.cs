using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Views;

/// <summary>
/// 通知悬浮窗（Avalonia 无边框置顶窗）：
/// 列表展示未读通知，每条右侧「已读」按钮 → MarkReadAsync 后条目消失（经 Changed 刷新）；
/// 空状态文案；Store.Changed 触发 200ms debounce 合并刷新；标题栏 BeginMoveDrag 拖拽、
/// 角部 Thumb 缩放；位置/大小由 <see cref="SuspensionWindowController"/> 持久化。
/// </summary>
public partial class NoticeSuspensionWindow : Window
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（限流，避免刷屏）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly INoticeStore _store = null!;
    private readonly DispatcherTimer _debounceTimer = null!;
    private int _refreshing;

    public NoticeSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含 store 构造
        InitializeComponent();
    }

    public NoticeSuspensionWindow(INoticeStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        _debounceTimer = new DispatcherTimer { Interval = RefreshDebounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RefreshAsync();
        };
        _store.Changed += OnStoreChanged;
        _ = RefreshAsync();
    }

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
        // 防并发重入（Refresh 本身在 UI 线程，GetUnreadAsync 同步完成，此为兜底）
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var unread = await _store.GetUnreadAsync();
            EmptyText.IsVisible = unread.Count == 0;
            NoticeList.ItemsSource = unread;
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

    private async void OnMarkReadClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { DataContext: NoticeItem item })
            {
                await _store.MarkReadAsync(item.Id);
                // 条目消失经 Changed → debounce 刷新完成
            }
        }
        catch
        {
            // 标记失败保持条目，不中断
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHideClick(object? sender, RoutedEventArgs e) => Hide();

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.Vector.X);
        Height = Math.Max(MinHeight, Height + e.Vector.Y);
    }
}
