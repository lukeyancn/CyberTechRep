using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIng.Plugin.Services.Overlays;
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

    /// <summary>「修正学科」候选：固定七学科 + 全部作业中已出现的其他学科（本条当前学科置顶）。</summary>
    public required IReadOnlyList<string> AvailableSubjects { get; init; }

    public string SelectedSubject => Item.Subject;

    public bool HasAttachments => Item.AttachmentIds.Count > 0;

    public string AttachmentText => $"附件 ×{Item.AttachmentIds.Count}";

    /// <summary>时间展示：列表仅含当天作业（仅当天语义），组内显示 HH:mm 即可。</summary>
    public string CreatedAtText => Item.CreatedAt.LocalDateTime.ToString("HH:mm");
}

/// <summary>
/// 作业悬浮窗（Avalonia 无边框置顶窗）：
/// 按学科分组（Expander 组头）展示，条目含正文、附件状态与「修正学科」下拉
/// （选择已有学科 → SetSubjectAsync，SubjectSource=Manual）；Changed 触发 200ms debounce 刷新；
/// 分组顺序按 <see cref="ClassIng.Shared.Models.OverlaySettings.HomeworkGroupOrder"/>（配置顺序优先，
/// 未配置的按字母序追加，空 = 全字母序）；设置广播触发刷新（分组顺序热生效）；
/// 标题栏 BeginMoveDrag 拖拽、角部 Thumb 缩放；位置/大小由 <see cref="SuspensionWindowController"/> 持久化。
/// </summary>
public partial class HomeworkSuspensionWindow : Window
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（与通知悬浮窗一致）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IHomeworkStore _store = null!;
    private readonly Func<IReadOnlyList<string>?>? _groupOrderProvider;
    private readonly DispatcherTimer _debounceTimer = null!;
    private int _refreshing;

    public HomeworkSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含 store 构造
        InitializeComponent();
    }

    public HomeworkSuspensionWindow(
        IHomeworkStore store,
        Func<IReadOnlyList<string>?>? groupOrderProvider = null,
        ISettingsService? settingsService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _groupOrderProvider = groupOrderProvider;
        InitializeComponent();
        _debounceTimer = new DispatcherTimer { Interval = RefreshDebounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RefreshAsync();
        };
        _store.Changed += OnStoreChanged;
        if (settingsService is not null)
        {
            // 设置变更（含分组顺序调整）→ debounce 刷新（SettingsChanged 可能在非 UI 线程触发）
            settingsService.SettingsChanged += OnSettingsChanged;
        }

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

    private void OnSettingsChanged(object? sender, AppSettings e) => OnStoreChanged(sender, null!);

    private void ScheduleRefresh()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>「修正学科」固定候选：无论作业列表里出现过哪些学科，这七项恒定可选。</summary>
    internal static readonly string[] BaseSubjects = ["语文", "数学", "英语", "物理", "化学", "生物", "其他"];

    internal async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            // 仅显示当天作业（按 CreatedAt 本地日期过滤）：
            // 复用存储代理提供的按日期桶查询接口（GetByDateAsync，CreatedAt 本地日期分桶）。
            var today = DateOnly.FromDateTime(DateTime.Now);
            var all = FilterToday(await _store.GetByDateAsync(today), today);

            // 固定七学科之外，作业里出现过的其他学科也追加进候选（含历史遗留分类）
            var extraSubjects = all.Select(i => i.Subject)
                .Where(s => !string.IsNullOrWhiteSpace(s) && !BaseSubjects.Contains(s, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
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
                            AvailableSubjects = BuildCandidates(i.Subject, extraSubjects)
                        })
                        .ToList()
                })
                .ToList();

            // 分组顺序：配置顺序优先（HomeworkGroupOrdering），未配置的按名称序追加；
            // 配置里多余/未知条目已由排序器安全忽略，兜底再按名称序补齐漏网组
            var ordered = HomeworkGroupOrdering.Sort(_groupOrderProvider?.Invoke(), groups.Select(g => g.Subject));
            var orderedGroups = new List<HomeworkGroup>(groups.Count);
            var remaining = new Dictionary<string, HomeworkGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                remaining[g.Subject.Trim()] = g;
            }

            foreach (var subject in ordered)
            {
                if (remaining.Remove(subject, out var group))
                {
                    orderedGroups.Add(group);
                }
            }

            orderedGroups.AddRange(remaining.Values.OrderBy(g => g.Subject, StringComparer.CurrentCulture));

            EmptyText.Text = "今天还没有作业";
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

    /// <summary>当天过滤（纯逻辑，可单测）：Created 转本地日期等于 today 才保留。</summary>
    internal static IReadOnlyList<HomeworkItem> FilterToday(IEnumerable<HomeworkItem> items, DateOnly today)
    {
        return items.Where(i => IsToday(i.CreatedAt, today))
            .OrderBy(i => i.CreatedAt)
            .ToList();
    }

    private static bool IsToday(DateTimeOffset createdAt, DateOnly today) =>
        DateOnly.FromDateTime(createdAt.LocalDateTime) == today;

    /// <summary>候选顺序：本条当前学科（不在固定列表时置顶）→ 固定七学科 → 其他已出现学科。</summary>
    private static IReadOnlyList<string> BuildCandidates(string currentSubject, List<string> extraSubjects)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(currentSubject)
            && !BaseSubjects.Contains(currentSubject, StringComparer.Ordinal))
        {
            candidates.Add(currentSubject);
        }

        candidates.AddRange(BaseSubjects);
        candidates.AddRange(extraSubjects.Where(s => !string.Equals(s, currentSubject, StringComparison.Ordinal)));
        return candidates;
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
