using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIng.Plugin.Services.MessageAccess;
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

/// <summary>作业悬浮窗条目视图（正文 + 附件状态；学科修正候选保留供非 UI 入口复用）。</summary>
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
/// 按学科分组（Expander 组头）展示，条目含正文与附件状态（学科修正下拉已从 UI 移除，
/// 底层 SetSubjectAsync 修正逻辑与候选构建保留）；Changed 触发 200ms debounce 刷新；
/// 分组顺序按 <see cref="ClassIng.Shared.Models.OverlaySettings.HomeworkGroupOrder"/>（配置顺序优先，
/// 未配置的按字母序追加，空 = 全字母序）；设置广播触发刷新（分组顺序热生效）；
/// 右上角「⋯」快捷菜单（与通知窗共用 <see cref="OverlayQuickMenu"/>）：
/// 置顶/固定/鼠标穿透开关即时生效并回写 ISettingsService；
/// 标题栏 BeginMoveDrag 拖拽、角部 Thumb 缩放；位置/大小由 <see cref="SuspensionWindowController"/> 持久化。
/// </summary>
public partial class HomeworkSuspensionWindow : Window
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（与通知悬浮窗一致）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IHomeworkStore _store = null!;
    private readonly IHomeworkSendService? _sendService;
    private readonly ISettingsService? _settingsService;
    private readonly Func<IReadOnlyList<string>?>? _groupOrderProvider;
    private readonly DispatcherTimer _debounceTimer = null!;
    private readonly OverlayQuickMenu _quickMenu = null!;
    private IReadOnlyList<HomeworkItem> _currentItems = [];
    private bool _sending;
    private int _refreshing;

    public HomeworkSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含 store 构造
        InitializeComponent();
    }

    public HomeworkSuspensionWindow(
        IHomeworkStore store,
        Func<IReadOnlyList<string>?>? groupOrderProvider = null,
        ISettingsService? settingsService = null,
        IHomeworkSendService? sendService = null,
        ISuspensionWindowController? overlays = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _groupOrderProvider = groupOrderProvider;
        _sendService = sendService;
        _settingsService = settingsService;
        InitializeComponent();
        // 右上角「⋯」快捷菜单（与通知窗共用 OverlayQuickMenu：置顶/固定/穿透，即时生效并回写设置）
        _quickMenu = new OverlayQuickMenu(
            settingsService, overlays, SuspensionWindowController.HomeworkKey,
            () => settingsService!.Current.Overlays.Homework, this);
        QuickMenuButton.Flyout = _quickMenu.Flyout;
        // 「整理并发送」开关：连接设置 HomeworkSendEnabled（热生效）；开关关闭时隐藏入口
        UpdateSendEntryVisibility();
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

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        // 发送开关热生效：SettingsChanged 可能在非 UI 线程触发，可见性更新回 UI 线程
        Dispatcher.UIThread.Post(UpdateSendEntryVisibility);
        OnStoreChanged(sender, null!);
    }

    /// <summary>「整理并发送」入口可见性：发送服务存在且开关开启才显示（设置热生效）。</summary>
    private void UpdateSendEntryVisibility()
        => SendDigestButton.IsVisible = IsSendEntryVisible(_sendService);

    /// <summary>发送入口可见性判定（纯逻辑，可单测）。</summary>
    internal static bool IsSendEntryVisible(IHomeworkSendService? sendService)
        => sendService is not null && sendService.IsEnabled;

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
            _currentItems = all;
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

    /// <summary>
    /// 删除条目：轻量二次确认 = 同一按钮两段式（首次点击进入「确认删除?」待确认态，
    /// 3 秒内再次点击才真正删除，超时自动复位）。理由：悬浮窗为无边框置顶小窗，
    /// 弹模态对话框会打断桌面常驻体验且易被置顶层级遮挡；两段式按钮与「修正学科」下拉同级紧凑，
    /// 误触概率低，刷新重建列表时待确认态自动失效（fail-safe）。
    /// </summary>
    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HomeworkRow row } button)
        {
            return;
        }

        if (button.Tag is not true)
        {
            // 第一次点击：进入待确认态（3 秒后自动复位）
            button.Tag = true;
            button.Content = "确认删除?";
            var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                if (button.Tag is true)
                {
                    button.Tag = null;
                    button.Content = "删除";
                }
            };
            revert.Start();
            return;
        }

        button.Tag = null;
        button.Content = "删除";
        try
        {
            await _store.DeleteAsync(row.Item.Id);
            // 列表刷新经 Changed → debounce 完成
        }
        catch
        {
            // 删除失败保持现状，不中断
        }
    }

    // ---------- 整理并发送（需求 2）----------

    private void OnSendDigestClick(object? sender, RoutedEventArgs e)
    {
        if (_sendService is null)
        {
            return;
        }

        // 清单预览：与实际发送内容同一格式化函数（同一份字符串），预览即所得
        SendPreviewText.Text = _currentItems.Count == 0
            ? "（今天还没有作业，无可发送内容）"
            : HomeworkDigestFormatter.Format(_currentItems);
        SendConfirmButton.IsEnabled = _currentItems.Count > 0;

        var groups = SettingsServiceGroupWhitelist();
        SendTargetText.Text = groups.Count == 0
            ? "目标群：白名单为空（请在 CyberTechRep 连接设置中配置群白名单），发送会失败"
            : $"目标群：{groups.Count} 个白名单群（{MaskGroups(groups)}），确认后逐群发送";
        SendResultText.IsVisible = false;
        SendResultText.Text = "";
        SendConfirmOverlay.IsVisible = true;
    }

    private void OnSendCancelClick(object? sender, RoutedEventArgs e) => HideSendOverlay();

    private void HideSendOverlay()
    {
        SendConfirmOverlay.IsVisible = false;
        SendResultText.IsVisible = false;
        SendResultText.Text = "";
    }

    private async void OnSendConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_sendService is null || _sending)
        {
            return; // 防重入：发送中忽略重复点击（按钮同时禁用）
        }

        _sending = true;
        SendConfirmButton.IsEnabled = false;
        SendConfirmButton.Content = "发送中…";
        SendCancelButton.IsEnabled = false;
        SendResultText.IsVisible = true;
        SendResultText.Text = "正在发送…";
        try
        {
            var digest = HomeworkDigestFormatter.Format(_currentItems);
            var results = await _sendService.SendTextToWhitelistedGroupsAsync(digest);
            var okCount = results.Count(r => r.Success);
            var lines = new List<string> { $"发送完成：成功 {okCount}/{results.Count} 群" };
            lines.AddRange(results.Where(r => !r.Success)
                .Select(r => $"群 {MaskGroupId(r.GroupOpenId)} 失败：{r.Error}"));
            SendResultText.Text = string.Join(Environment.NewLine, lines);
            if (results.Count > 0 && okCount == results.Count)
            {
                // 全部成功：短暂展示结果后自动收起
                var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    HideSendOverlay();
                };
                close.Start();
            }
        }
        catch (Exception ex)
        {
            // 失败明确回 UI，不静默
            SendResultText.Text = $"发送失败：{ex.Message}";
        }
        finally
        {
            _sending = false;
            SendConfirmButton.IsEnabled = true;
            SendConfirmButton.Content = "发送";
            SendCancelButton.IsEnabled = true;
        }
    }

    private IReadOnlyList<string> SettingsServiceGroupWhitelist()
    {
        try
        {
            return _settingsService?.Current.Connection.GroupWhitelist ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string MaskGroups(IReadOnlyList<string> groups)
    {
        const int maxShow = 3;
        var shown = groups.Take(maxShow).Select(MaskGroupId);
        return groups.Count > maxShow
            ? string.Join("、", shown) + $" 等 {groups.Count} 个"
            : string.Join("、", shown);
    }

    private static string MaskGroupId(string groupOpenId) =>
        string.IsNullOrEmpty(groupOpenId) ? "(未配置)" :
        groupOpenId.Length <= 10 ? groupOpenId : groupOpenId[..10] + "…";

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
