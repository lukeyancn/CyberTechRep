using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Views;

/// <summary>作业悬浮窗分组条目（按学科分组的组头视图）。</summary>
public sealed class HomeworkGroup
{
    public required string Subject { get; init; }

    public required IReadOnlyList<HomeworkRow> Items { get; init; }
}

/// <summary>作业悬浮窗条目视图（正文 + 附件状态 + 修正学科下拉）。</summary>
public sealed class HomeworkRow
{
    public required HomeworkItem Item { get; init; }

    /// <summary>「修正学科」候选：全部作业中已出现的学科（含本条当前学科）。</summary>
    public required IReadOnlyList<string> AvailableSubjects { get; init; }

    public string SelectedSubject => Item.Subject;

    public bool HasAttachments => Item.AttachmentIds.Count > 0;

    public string AttachmentText => $"附件 ×{Item.AttachmentIds.Count}";

    public string CreatedAtText => Item.CreatedAt.ToString("MM-dd HH:mm");
}

/// <summary>
/// 作业悬浮窗（Avalonia 无边框置顶窗）：
/// 按学科分组（Expander 组头）展示，条目含正文、附件状态与「修正学科」下拉
/// （选择已有学科 → SetSubjectAsync，SubjectSource=Manual）；Changed 触发 200ms debounce 刷新；
/// 标题栏 BeginMoveDrag 拖拽、角部 Thumb 缩放；位置/大小由 <see cref="SuspensionWindowController"/> 持久化。
/// </summary>
public partial class HomeworkSuspensionWindow : Window
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（与通知悬浮窗一致）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IHomeworkStore _store = null!;
    private readonly DispatcherTimer _debounceTimer = null!;
    private int _refreshing;

    public HomeworkSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含 store 构造
        InitializeComponent();
    }

    public HomeworkSuspensionWindow(IHomeworkStore store)
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

    /// <summary>当前分组数据源（测试用）。</summary>
    public System.Collections.IEnumerable? VisibleGroups => GroupList?.ItemsSource;

    private void OnStoreChanged(object? sender, HomeworkItem e)
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

    private void ScheduleRefresh()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    internal async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var all = await _store.GetAllAsync();
            var subjects = all.Select(i => i.Subject)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.CurrentCulture)
                .ToList();

            var groups = all
                .GroupBy(i => i.Subject, StringComparer.OrdinalIgnoreCase)
                .Select(g => new HomeworkGroup
                {
                    Subject = g.Key,
                    Items = g.OrderBy(i => i.CreatedAt)
                        .Select(i => new HomeworkRow
                        {
                            Item = i,
                            AvailableSubjects = subjects.Contains(i.Subject, StringComparer.OrdinalIgnoreCase)
                                ? subjects
                                : [i.Subject, .. subjects]
                        })
                        .ToList()
                })
                .OrderBy(g => g.Subject, StringComparer.CurrentCulture)
                .ToList();

            EmptyText.IsVisible = all.Count == 0;
            GroupList.ItemsSource = groups;
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

    private async void OnSubjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (sender is ComboBox { DataContext: HomeworkRow row } comboBox
                && comboBox.SelectedItem is string subject
                && !string.Equals(subject, row.Item.Subject, StringComparison.Ordinal))
            {
                await _store.SetSubjectAsync(row.Item.Id, subject);
                // 分组归属变化经 Changed → debounce 刷新完成
            }
        }
        catch
        {
            // 修正失败保持原学科，不中断
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
