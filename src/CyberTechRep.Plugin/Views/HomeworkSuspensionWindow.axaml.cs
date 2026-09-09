using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Views;

/// <summary>
/// 作业悬浮窗学科文档视图（需求 1）：每个学科一个撑满区块的方块，
/// <see cref="Text"/> 双向绑定到可编辑文本容器；显示态点击进入编辑态，编辑态失焦/停顿回写存档。
/// </summary>
public sealed class HomeworkDocumentView : INotifyPropertyChanged
{
    private string _text = "";
    private bool _isEditing;
    private string _clearButtonText = "清空";
    private string _sourceSummary = "";

    /// <summary>学科名（组头与落档键）。</summary>
    public required string Subject { get; init; }

    /// <summary>归档日（编辑回写与「转为通知」定位用）。</summary>
    public required DateOnly Date { get; init; }

    /// <summary>渲染文本（ManualText 优先，否则按条目拼接）；编辑态为用户正在改写的文本。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            _text = value;
            Raise(nameof(Text));
        }
    }

    /// <summary>是否处于编辑态（显示态为可选中的只读文本，点击进入编辑）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
            {
                return;
            }

            _isEditing = value;
            Raise(nameof(IsEditing));
            Raise(nameof(EditHint));
        }
    }

    /// <summary>来源与时间元信息摘要（存档条目里保留，折叠成一行展示）。</summary>
    public string SourceSummary
    {
        get => _sourceSummary;
        init
        {
            _sourceSummary = value;
            Raise(nameof(SourceSummary));
            Raise(nameof(HasSourceSummary));
        }
    }

    public bool HasSourceSummary => !string.IsNullOrEmpty(_sourceSummary);

    /// <summary>「清空」按钮两段式确认文案。</summary>
    public string ClearButtonText
    {
        get => _clearButtonText;
        set
        {
            if (string.Equals(_clearButtonText, value, StringComparison.Ordinal))
            {
                return;
            }

            _clearButtonText = value;
            Raise(nameof(ClearButtonText));
        }
    }

    /// <summary>进入编辑态时的渲染文本（三方合并基线：区分「用户删改」与「编辑期间新到的消息」）。</summary>
    public string? EditBaseline { get; set; }

    public string EditHint => IsEditing ? "编辑中…停顿后自动保存" : "点击文字即可编辑";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>来源摘要（纯函数，可单测）：条数 + 前几位发送者与时间。</summary>
    internal static string BuildSourceSummary(HomeworkDocument document)
    {
        var entries = document.Entries;
        if (entries.Count == 0)
        {
            return document.ManualText is { Length: > 0 } ? "手工编辑（无消息来源）" : "";
        }

        const int maxShown = 3;
        var parts = entries
            .OrderBy(e => e.CreatedAt)
            .Take(maxShown)
            .Select(e =>
            {
                var label = string.IsNullOrWhiteSpace(e.SenderLabel) ? "成员" : e.SenderLabel.Trim();
                return $"{label} {e.CreatedAt.LocalDateTime:HH:mm}";
            })
            .ToList();
        var suffix = entries.Count > maxShown ? $" 等 {entries.Count} 条来源" : "";
        return $"{entries.Count} 条来源 · {string.Join("、", parts)}{suffix}";
    }
}

/// <summary>「整理并发送·确认」里的常态化作业勾选项（需求 3）。</summary>
public sealed class StandingHomeworkView : INotifyPropertyChanged
{
    private bool _isChecked;

    public required StandingHomeworkItem Item { get; init; }

    /// <summary>勾选状态：勾选即时进入预览，取消即从预览移除；点「发送」才落档。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public string DisplayText => $"{Item.Subject}：{Item.Content}";

    public string Tooltip => $"勾选后写入「{Item.Subject}」作业文档末尾（点「发送」才落档）";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 作业悬浮窗（Avalonia 无边框置顶窗）：
/// <para>
/// <b>需求 1 文档化</b>：按学科分组，每个学科区块内只有一个撑满区块的文档方块——该学科当天作业
/// 聚合为一份连续文档（<see cref="IHomeworkStore.GetDocumentsAsync"/> 的渲染文本）；
/// 点击文档进入编辑态，失焦或停顿 600ms 实时回写存档（<see cref="IHomeworkStore.SaveDocumentTextAsync"/>，
/// 存档为单一事实源）；编辑期间挂起列表重建，避免刷新打断输入。
/// </para>
/// <para>
/// <b>需求 3 常态化作业</b>：「整理并发送·确认」浮层列出启用的常态化作业并支持勾选，
/// 勾选即时进预览、取消即移除，点「发送」才写入该学科文档末尾（IsStanding 条目）。
/// </para>
/// <para>
/// <b>需求 5 换类</b>：文档方块提供「转为通知」，整份学科文档迁入通知存档。
/// </para>
/// 其余行为保持原样：仅显示当天作业、分组顺序按配置（HomeworkGroupOrder）、Changed 触发
/// 200ms debounce 刷新、右上角「⋯」快捷菜单（置顶/固定/穿透）、拖拽与角部缩放、整理并发送入口。
/// </summary>
public partial class HomeworkSuspensionWindow : Window
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（与通知悬浮窗一致）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>文档编辑回写存档的防抖间隔（停顿即存，不逐字符写盘）。</summary>
    internal static readonly TimeSpan DocumentSaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly IHomeworkStore _store = null!;
    private readonly IHomeworkSendService? _sendService;
    private readonly ISettingsService? _settingsService;
    private readonly IMessageReclassifyService? _reclassify;
    private readonly Func<IReadOnlyList<string>?>? _groupOrderProvider;
    private readonly DispatcherTimer _debounceTimer = null!;
    private readonly DispatcherTimer _documentSaveTimer = null!;
    private readonly OverlayQuickMenu _quickMenu = null!;
    private IReadOnlyList<HomeworkDocument> _documents = [];
    private IReadOnlyList<StandingHomeworkView> _standingViews = [];
    private HomeworkDocumentView? _editing;
    private HomeworkDocumentView? _pendingSave;
    private bool _refreshPending;
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
        ISuspensionWindowController? overlays = null,
        IMessageReclassifyService? reclassify = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _groupOrderProvider = groupOrderProvider;
        _sendService = sendService;
        _settingsService = settingsService;
        _reclassify = reclassify;
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
        _documentSaveTimer = new DispatcherTimer { Interval = DocumentSaveDebounce };
        _documentSaveTimer.Tick += async (_, _) =>
        {
            _documentSaveTimer.Stop();
            var view = _pendingSave ?? _editing;
            if (view is not null)
            {
                await PersistDocumentTextAsync(view);
            }
        };
        _store.Changed += OnStoreItemChanged;
        _store.DocumentChanged += OnStoreDocumentChanged;
        if (settingsService is not null)
        {
            // 设置变更（含分组顺序调整）→ debounce 刷新（SettingsChanged 可能在非 UI 线程触发）
            settingsService.SettingsChanged += OnSettingsChanged;
        }

        _ = RefreshAsync();
    }

    /// <summary>当前分组数据源（测试用）。</summary>
    public System.Collections.IEnumerable? VisibleGroups => GroupList?.ItemsSource;

    /// <summary>当前文档视图（测试用）。</summary>
    internal IReadOnlyList<HomeworkDocumentView> VisibleDocuments =>
        GroupList?.ItemsSource as IReadOnlyList<HomeworkDocumentView> ?? [];

    private void OnStoreItemChanged(object? sender, HomeworkItem e) => ScheduleRefreshFromAnyThread();

    private void OnStoreDocumentChanged(object? sender, HomeworkDocument e) => ScheduleRefreshFromAnyThread();

    private void ScheduleRefreshFromAnyThread()
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
        ScheduleRefreshFromAnyThread();
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
        // 编辑态挂起重建：刷新会替换 ItemsSource 导致输入框失焦、光标丢失
        if (_editing is not null)
        {
            _refreshPending = true;
            return;
        }

        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var documents = (await _store.GetDocumentsAsync(today))
                .Where(d => !d.IsEmpty)
                .ToList();
            _documents = documents;

            var ordered = HomeworkGroupOrdering.Sort(
                _groupOrderProvider?.Invoke(), documents.Select(d => d.Subject));
            var remaining = new Dictionary<string, HomeworkDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents)
            {
                remaining[document.Subject.Trim()] = document;
            }

            var views = new List<HomeworkDocumentView>(documents.Count);
            foreach (var subject in ordered)
            {
                if (remaining.Remove(subject, out var document))
                {
                    views.Add(BuildView(document));
                }
            }

            views.AddRange(remaining.Values
                .OrderBy(d => d.Subject, StringComparer.CurrentCulture)
                .Select(BuildView));

            EmptyText.Text = "今天还没有作业";
            EmptyText.IsVisible = views.Count == 0;
            GroupList.ItemsSource = views;
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

    private static HomeworkDocumentView BuildView(HomeworkDocument document) => new()
    {
        Subject = document.Subject,
        Date = document.Date,
        Text = document.Render,
        SourceSummary = HomeworkDocumentView.BuildSourceSummary(document)
    };

    /// <summary>当天过滤（纯逻辑，可单测）：Created 转本地日期等于 today 才保留。</summary>
    internal static IReadOnlyList<HomeworkItem> FilterToday(IEnumerable<HomeworkItem> items, DateOnly today)
    {
        return items.Where(i => IsToday(i.CreatedAt, today))
            .OrderBy(i => i.CreatedAt)
            .ToList();
    }

    private static bool IsToday(DateTimeOffset createdAt, DateOnly today) =>
        DateOnly.FromDateTime(createdAt.LocalDateTime) == today;

    // ---------- 需求 1：文档点击编辑 / 实时回写 ----------

    private void OnDocumentPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: HomeworkDocumentView view }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (ReferenceEquals(_editing, view))
        {
            return;
        }

        // 切换编辑目标前先落档上一个（只写存档、不改编辑态，避免重建列表打断即将进入的编辑）
        var previous = _editing;
        if (previous is not null)
        {
            previous.IsEditing = false;
            _ = PersistDocumentTextAsync(previous);
        }

        _editing = view;
        view.EditBaseline = view.Text;
        view.IsEditing = true;
        FocusEditor(sender);
    }

    /// <summary>进入编辑态后把焦点与光标放到同方块内的编辑框末尾。</summary>
    private static void FocusEditor(object? displayControl)
    {
        if (displayControl is not Control control || control.Parent is not Panel panel)
        {
            return;
        }

        var editor = panel.Children.OfType<TextBox>().FirstOrDefault();
        if (editor is null)
        {
            return;
        }

        editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;
    }

    private void OnDocumentTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not Control { DataContext: HomeworkDocumentView view })
        {
            return;
        }

        _pendingSave = view;
        _documentSaveTimer.Stop();
        _documentSaveTimer.Start();
    }

    private async void OnDocumentEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        _documentSaveTimer.Stop();
        if (sender is not Control { DataContext: HomeworkDocumentView view })
        {
            return;
        }

        await PersistDocumentTextAsync(view);
        view.IsEditing = false;
        if (ReferenceEquals(_editing, view))
        {
            _editing = null;
        }

        // 编辑期间挂起的刷新（新消息/其他窗口改动）现在补做
        if (_refreshPending && _editing is null)
        {
            _refreshPending = false;
            await RefreshAsync();
        }
    }

    /// <summary>
    /// 把文档文本回写存档（存档为单一事实源）；空文本 = 清除手工文本、回到按条目渲染。
    /// 传入进入编辑态时的基线做三方合并：编辑期间新到达的消息行补齐到末尾，用户删改保留。
    /// 保存失败保留用户输入（不静默丢改动），下次失焦/停顿再试。
    /// </summary>
    private async Task PersistDocumentTextAsync(HomeworkDocumentView view)
    {
        if (ReferenceEquals(_pendingSave, view))
        {
            _pendingSave = null;
        }

        try
        {
            await _store.SaveDocumentTextAsync(
                view.Date, view.Subject, view.Text, CancellationToken.None, view.EditBaseline);
        }
        catch
        {
            // 保留用户输入，不静默丢改动
        }
    }

    // ---------- 需求 5：文档 → 通知 ----------

    private async void OnDocumentToNoticeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: HomeworkDocumentView view })
        {
            return;
        }

        if (_reclassify is null)
        {
            return;
        }

        try
        {
            // 编辑中的改动先落档，再整体迁移，避免丢改
            if (_editing is not null)
            {
                await PersistDocumentTextAsync(_editing);
            }

            await _reclassify.MoveDocumentToNoticeAsync(view.Date, view.Subject);
            // 存储事件 → debounce 刷新（作业方块消失、通知悬浮窗出现）
        }
        catch
        {
            // 迁移失败保持现状，不中断悬浮窗
        }
    }

    // ---------- 清空文档（保留既有删除能力，两段式确认） ----------

    private async void OnClearDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HomeworkDocumentView view } button)
        {
            return;
        }

        if (button.Tag is not true)
        {
            button.Tag = true;
            view.ClearButtonText = "确认清空?";
            var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                if (button.Tag is true)
                {
                    button.Tag = null;
                    view.ClearButtonText = "清空";
                }
            };
            revert.Start();
            return;
        }

        button.Tag = null;
        view.ClearButtonText = "清空";
        try
        {
            await _store.RemoveDocumentAsync(view.Date, view.Subject);
            // 列表刷新经 DocumentChanged → debounce 完成
        }
        catch
        {
            // 删除失败保持现状，不中断
        }
    }

    // ---------- 整理并发送（需求 2 + 需求 3）----------

    private void OnSendDigestClick(object? sender, RoutedEventArgs e)
    {
        if (_sendService is null)
        {
            return;
        }

        BuildStandingViews();
        SendPreviewText.Text = BuildDigest();
        SendConfirmButton.IsEnabled = HasSendableContent();

        var groups = SettingsServiceGroupWhitelist();
        SendTargetText.Text = groups.Count == 0
            ? "目标群：白名单为空（请在 CyberTechRep 连接设置中配置目标群），发送会失败"
            : $"目标群：{groups.Count} 个目标群（{MaskGroups(groups)}），确认后逐群发送";
        SendResultText.IsVisible = false;
        SendResultText.Text = "";
        SendConfirmOverlay.IsVisible = true;
    }

    /// <summary>勾选列表数据源（测试用）。</summary>
    internal IReadOnlyList<StandingHomeworkView> StandingViews => _standingViews;

    /// <summary>从设置构建常态化作业勾选项（仅启用且学科/内容非空的条目；每次打开确认窗重置为未勾选）。</summary>
    private void BuildStandingViews()
    {
        var items = _settingsService?.Current.StandingHomework.Items ?? [];
        _standingViews = items
            .Where(i => i.Enabled
                && !string.IsNullOrWhiteSpace(i.Subject)
                && !string.IsNullOrWhiteSpace(i.Content))
            .Select(i => new StandingHomeworkView { Item = i })
            .ToList();
        StandingList.ItemsSource = _standingViews;
        StandingList.IsVisible = _standingViews.Count > 0;
        StandingSection.IsVisible = _standingViews.Count > 0;
    }

    private void OnStandingItemToggled(object? sender, RoutedEventArgs e)
    {
        // 勾选即时刷新预览（取消勾选即从预览移除）；落档仍只在点「发送」时发生
        SendPreviewText.Text = BuildDigest();
        SendConfirmButton.IsEnabled = HasSendableContent();
    }

    private bool HasSendableContent() =>
        _documents.Any(d => !d.IsEmpty) || _standingViews.Any(v => v.IsChecked);

    /// <summary>预览与实际发送共用的同一份清单文本（预览即所得）。</summary>
    private string BuildDigest()
    {
        var checkedItems = _standingViews.Where(v => v.IsChecked).Select(v => v.Item).ToList();
        if (_documents.Count == 0 && checkedItems.Count == 0)
        {
            return "（今天还没有作业，无可发送内容）";
        }

        return HomeworkDigestFormatter.FormatDocuments(_documents, checkedItems);
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
            // 预览即所得：先取用户看到的清单文本（含勾选的常态化作业行），再落档。
            // 顺序不可颠倒——落档后文档已含常态化行，重新格式化会再追加一遍（曾经的重复发送根因）。
            var digest = BuildDigest();

            // 需求 3 落档时机：点「发送」才把勾选的常态化作业写入该学科文档末尾
            // （勾选只影响预览）。写入幂等：同一天同学科同内容已存在时由合并器判为完全重复而跳过。
            var standingError = await ApplyStandingHomeworkAsync();

            var results = await _sendService.SendTextToTargetGroupsAsync(digest);
            var okCount = results.Count(r => r.Success);
            var lines = new List<string> { $"发送完成：成功 {okCount}/{results.Count} 群" };
            if (standingError is not null)
            {
                lines.Add(standingError);
            }

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

    /// <summary>
    /// 把勾选的常态化作业写入对应学科文档末尾（IsStanding 条目，来源元信息标为常态化）。
    /// 返回 null 表示全部成功，否则返回可展示的失败摘要（不阻断发送）。
    /// </summary>
    private async Task<string?> ApplyStandingHomeworkAsync()
    {
        var checkedItems = _standingViews.Where(v => v.IsChecked).Select(v => v.Item).ToList();
        if (checkedItems.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var failed = new List<string>();
        foreach (var item in checkedItems)
        {
            try
            {
                await _store.AppendDocumentEntryAsync(item.Subject, new HomeworkDocumentEntry
                {
                    SourceMessageIds = [],
                    MemberOpenId = "",
                    SenderLabel = "常态化作业",
                    Text = item.Content.Trim(),
                    CreatedAt = now,
                    IsStanding = true
                });
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Subject}：{ex.Message}");
            }
        }

        // 落档后刷新本地文档副本（发送内容与预览同源，仍用 BuildDigest 的同一份字符串）
        _documents = (await _store.GetDocumentsAsync(DateOnly.FromDateTime(now.LocalDateTime)))
            .Where(d => !d.IsEmpty)
            .ToList();
        return failed.Count == 0 ? null : $"常态化作业落档失败：{string.Join("；", failed)}";
    }

    private IReadOnlyList<string> SettingsServiceGroupWhitelist()
    {
        try
        {
            return _settingsService?.Current.Connection.TargetGroupOpenIds ?? [];
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
