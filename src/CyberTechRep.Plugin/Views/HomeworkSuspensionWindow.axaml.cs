using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Services.Stores;
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

    /// <summary>进入编辑态时的渲染文本（编辑器基线：仅用于判断用户有没有改动）。</summary>
    public string? EditBaseline { get; set; }

    /// <summary>
    /// 进入编辑态那一刻文档里已有的条目 id 集合（编辑锚点，见 <see cref="IHomeworkStore.SaveDocumentTextAsync"/>）：
    /// 落档时只有集合外的条目（= 编辑期间新到的消息）才把行补进编辑稿。
    /// 整个编辑态期间保持不变——落档后若把新条目也并进来，下一拍就会把它们当「旧行」丢掉。
    /// </summary>
    internal IReadOnlyCollection<Guid> KnownEntryIds { get; set; } = [];

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
    private bool _isLanded;

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

    /// <summary>
    /// 今天是否已落档（该学科文档里已存在同内容的常态化条目）。
    /// <para>
    /// 已落档的条目显示为「已勾选且不可取消」：它已在当天文档里，本来就会随清单发出，
    /// 取消勾选并不能把它从文档中移除。旧实现每次打开确认窗一律重置为未勾选，
    /// 与「文档里已经有这一行」的事实不一致，老师无法判断是否已计入（行为缺陷）。
    /// </para>
    /// </summary>
    public bool IsLanded
    {
        get => _isLanded;
        set
        {
            if (_isLanded == value)
            {
                return;
            }

            _isLanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsToggleEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
        }
    }

    /// <summary>可否手工勾选/取消：已落档的条目不可取消（取消也无法从文档移除）。</summary>
    public bool IsToggleEnabled => !IsLanded;

    public string DisplayText => IsLanded
        ? $"{Item.Subject}：{Item.Content}（今天已落档）"
        : $"{Item.Subject}：{Item.Content}";

    public string Tooltip => IsLanded
        ? $"今天已写入「{Item.Subject}」作业文档（随清单一起发出），不能在这里取消"
        : $"勾选后写入「{Item.Subject}」作业文档末尾（点「发送」才落档）";

    /// <summary>稳定的落档匹配键：学科 + 归一化内容（与合并器同一归一化口径）。</summary>
    internal static string LandingKey(string? subject, string? content) =>
        HomeworkDocumentMerger.Normalize(subject ?? "")
        + "\u0000"
        + HomeworkDocumentMerger.Normalize(content ?? "");

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
public partial class HomeworkSuspensionWindow : Window, IOverlayContentFontSizeAware
{
    /// <summary>Changed 事件合并刷新的 debounce 间隔（与通知悬浮窗一致）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>文档编辑回写存档的防抖间隔（停顿即存，不逐字符写盘）。</summary>
    internal static readonly TimeSpan DocumentSaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly IHomeworkStore _store = null!;
    private readonly IHomeworkSendService? _sendService;
    private readonly ISettingsService? _settingsService;
    private readonly IMessageReclassifyService? _reclassify;
    private readonly ISuspensionWindowController? _overlays;
    private readonly Func<IReadOnlyList<string>?>? _groupOrderProvider;
    private readonly DispatcherTimer _debounceTimer = null!;
    private readonly DispatcherTimer _documentSaveTimer = null!;
    private readonly OverlayQuickMenu _quickMenu = null!;
    private IReadOnlyList<HomeworkDocument> _documents = [];
    private IReadOnlyList<StandingHomeworkView> _standingViews = [];
    private HomeworkDocumentView? _editing;

    /// <summary>待发文本是否被手动编辑过（编辑后勾选变更不覆盖文本，需点「重新生成清单」重建）。</summary>
    internal bool SendPreviewEdited =>
        !string.Equals(SendPreviewEditor.Text ?? "", _generatedPreview, StringComparison.Ordinal);

    /// <summary>当前待发文本是否为「今天还没有作业」占位（未手动编辑时不可发送）。</summary>
    private bool _sendPreviewPlaceholder;

    /// <summary>上一次程序生成的清单文本（与编辑框当前文本比对即可判断是否被手动改过）。</summary>
    private string _generatedPreview = "";
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
        _overlays = overlays;
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
            await FlushPendingDocumentSaveAsync();
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

    /// <summary>
    /// 作业文档正文（可选中/可编辑）字号跟随悬浮窗设置「字号」。
    /// <para>
    /// 走窗口资源 + 样式动态资源：宿主主题（Fluent）给 TextBox/SelectableTextBlock 的 ControlTheme
    /// 自带 FontSize，优先级高于窗口级属性继承——只在窗口上设字号时正文不会变（用户实测缺陷）。
    /// </para>
    /// </summary>
    public void ApplyContentFontSize(double fontSize) =>
        Resources["CyberTechRepOverlayContentFontSize"] = fontSize;

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
        SourceSummary = HomeworkDocumentView.BuildSourceSummary(document),
        // 编辑锚点：进入编辑态时已有的条目 id（落档时只有锚点外的条目才算「编辑期间新到」）
        KnownEntryIds = document.Entries.Select(e => e.Id).ToList()
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

    /// <summary>
    /// 点击文档文本进入编辑态（处理器挂在包着文本的透明 Border 上）。
    /// <para>
    /// 必须用 <see cref="Tapped"/> 而不是 <c>PointerPressed</c>：<see cref="SelectableTextBlock"/>
    /// 为了做选中/复制在 <c>OnPointerPressed</c> 里把事件标记为已处理（并捕获指针），
    /// XAML 事件属性默认不看已处理事件，挂在它身上的 PointerPressed 永远不触发。
    /// Tapped 只在「按下+抬起且未拖动」时触发，拖动选中复制仍然照常。
    /// </para>
    /// <para>
    /// 另外 <see cref="SelectableTextBlock"/> 只在文字字形上命中，方块内空白处点不到它，
    /// 所以外面套一层透明 Border 承接整块区域的点击。
    /// </para>
    /// </summary>
    private void OnDocumentTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: HomeworkDocumentView view }
            || ReferenceEquals(_editing, view))
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
        // 编辑需要真实键盘输入：临时让悬浮窗可被激活（WS_EX_NOACTIVATE 的窗口收不到键盘消息）
        _overlays?.SetOverlayEditing(SuspensionWindowController.HomeworkKey, editing: true);
        if (!FocusEditor(sender))
        {
            // 真实窗口上「清除 WS_EX_NOACTIVATE + 置前台」到 Avalonia 收到激活存在消息时序，
            // 首次聚焦可能因窗口尚未激活而落空：补一次输入优先级重试。
            Dispatcher.UIThread.Post(() => FocusEditor(sender), DispatcherPriority.Input);
        }
    }

    /// <summary>进入编辑态后把焦点与光标放到同方块内的编辑框末尾；返回是否成功聚焦。</summary>
    private static bool FocusEditor(object? displayControl)
    {
        if (displayControl is not Control control || control.Parent is not Panel panel)
        {
            return false;
        }

        var editor = panel.Children.OfType<TextBox>().FirstOrDefault();
        if (editor is null)
        {
            return false;
        }

        var focused = editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;
        return focused;
    }

    private void OnDocumentTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not Control { DataContext: HomeworkDocumentView view })
        {
            return;
        }

        // 只有编辑态下的文本变化才算用户输入：列表重建时绑定回填 Text 同样会触发 TextChanged，
        // 那不是用户编辑，绝不能据此把渲染文本写进存档（否则文档被冻结成手工文本，
        // 并引发「写存档 → 重建列表 → 再写存档」的反复重建卡顿）
        if (!view.IsEditing)
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
            // 退出编辑态：恢复「不抢焦点、钉在桌面层」契约（切换到别的文档时不动，
            // 那边的进入编辑态逻辑已重新开启）
            _overlays?.SetOverlayEditing(SuspensionWindowController.HomeworkKey, editing: false);
        }

        // 编辑期间挂起的刷新（新消息/其他窗口改动）现在补做
        if (_refreshPending && _editing is null)
        {
            _refreshPending = false;
            await RefreshAsync();
        }
    }

    /// <summary>立即落档待保存的编辑稿（防抖计时器触发时调用；测试也用它跳过计时器）。</summary>
    internal Task FlushPendingDocumentSaveAsync()
    {
        var view = _pendingSave ?? _editing;
        return view is null ? Task.CompletedTask : PersistDocumentTextAsync(view);
    }

    /// <summary>
    /// 把文档文本回写存档（存档为单一事实源）；空文本 = 清除手工文本、回到按条目渲染。
    /// 传入进入编辑态时的条目 id 锚点做三方合并：编辑期间新到的消息行补齐到末尾，
    /// 用户对旧行的删改保留。与基线相同（点进编辑态没输入 / 改回原样）不写存档，保持「按条目渲染」口径。
    /// 保存失败保留用户输入（不静默丢改动），下次失焦/停顿再试。
    /// </summary>
    private async Task PersistDocumentTextAsync(HomeworkDocumentView view)
    {
        if (ReferenceEquals(_pendingSave, view))
        {
            _pendingSave = null;
        }

        var text = view.Text;
        var baseline = view.EditBaseline;
        if (!HasUserChanges(text, baseline))
        {
            return;
        }

        try
        {
            await _store.SaveDocumentTextAsync(
                view.Date, view.Subject, text, CancellationToken.None, view.KnownEntryIds);
            // 落档成功后以「已落档的那一版」为新基线：用户再改回上一版也能正确判定为有改动
            // （锚点不动：编辑期间新到的条目在整个编辑态里都要继续按「新行」补齐，不能被吞掉）
            view.EditBaseline = text;
        }
        catch
        {
            // 保留用户输入，不静默丢改动
        }
    }

    /// <summary>编辑稿与进入编辑态时的基线是否不同（相同 = 用户没改，无需回写存档）。</summary>
    internal static bool HasUserChanges(string? editorText, string? editBaseline)
        => !string.Equals(editorText, editBaseline, StringComparison.Ordinal);

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

        OpenSendOverlay();
    }

    /// <summary>
    /// 打开「整理并发送 · 确认」浮层：重建常态化作业勾选项、刷新预览与目标群提示。
    /// 抽成内部入口供测试驱动（与按钮点击同一路径）。
    /// </summary>
    internal void OpenSendOverlay()
    {
        BuildStandingViews();
        // 每次打开都给一份生成的清单，老师随后可直接在编辑框里改
        ApplyGeneratedPreview();

        var groups = SettingsServiceGroupWhitelist();
        SendTargetText.Text = groups.Count == 0
            ? "目标群：白名单为空（请在 CyberTechRep 连接设置中配置目标群），发送会失败"
            : $"目标群：{groups.Count} 个目标群（{MaskGroups(groups)}），确认后逐群发送";
        SendResultText.IsVisible = false;
        SendResultText.Text = "";
        SendConfirmOverlay.IsVisible = true;

        // 待发消息要能直接打字：临时解除 WS_EX_NOACTIVATE 并置前（与文档编辑态同一机制）——
        // 无边框悬浮窗默认不抢焦点、收不到键盘消息（此前「编辑态打不了字」的同类问题）
        _overlays?.SetOverlayEditing(SuspensionWindowController.HomeworkKey, editing: true);
        var focused = SendPreviewEditor.Focus();
        SendPreviewEditor.CaretIndex = SendPreviewEditor.Text?.Length ?? 0;
        if (!focused)
        {
            // 宿主窗口激活存在消息时序：首次聚焦可能落空，补一次输入优先级重试
            Dispatcher.UIThread.Post(() => SendPreviewEditor.Focus(), DispatcherPriority.Input);
        }
    }

    /// <summary>确认浮层里的待发文本（测试用；待发即实际发送内容）。</summary>
    internal string SendPreview => SendPreviewEditor.Text ?? "";

    /// <summary>「今天还没有作业」占位文本（未手动编辑时不可发送，避免把提示语发出去）。</summary>
    internal const string EmptyDigestText = "（今天还没有作业，无可发送内容）";

    /// <summary>把生成的清单写入待发编辑框（并记住生成文本，供「是否被手动改过」判定）。</summary>
    private void ApplyGeneratedPreview()
    {
        var text = BuildDigest();
        _generatedPreview = text;
        _sendPreviewPlaceholder = string.Equals(text, EmptyDigestText, StringComparison.Ordinal);
        SendPreviewEditor.Text = text;
        SendConfirmButton.IsEnabled = CanSendPreview();
    }

    /// <summary>待发文本变化：刷新发送按钮可用性（文本框由老师直接编辑，无需额外状态）。</summary>
    private void OnSendPreviewTextChanged(object? sender, TextChangedEventArgs e)
        => SendConfirmButton.IsEnabled = CanSendPreview();

    /// <summary>按当前作业与勾选项重建清单（会覆盖手动编辑；XAML 按钮与测试共用）。</summary>
    internal void RegeneratePreview() => ApplyGeneratedPreview();

    private void OnRegeneratePreviewClick(object? sender, RoutedEventArgs e) => RegeneratePreview();

    /// <summary>待发编辑框（测试用：模拟老师在确认窗里直接编辑待发消息）。</summary>
    internal TextBox SendPreviewEditorForTest => SendPreviewEditor;

    /// <summary>
    /// 可否发送：待发文本非空；未手动编辑时「今天还没有作业」占位不可发送（避免把提示语发出去），
    /// 手动编辑过则以老师的文本为准（哪怕当天没有作业，也可以直接写一条发出去）。
    /// </summary>
    internal bool CanSendPreview()
    {
        var text = SendPreviewEditor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return SendPreviewEdited || !_sendPreviewPlaceholder;
    }

    /// <summary>勾选列表数据源（测试用）。</summary>
    internal IReadOnlyList<StandingHomeworkView> StandingViews => _standingViews;

    /// <summary>从设置构建常态化作业勾选项（仅启用且学科/内容非空的条目）。</summary>
    private void BuildStandingViews()
    {
        var items = _settingsService?.Current.StandingHomework.Items ?? [];
        var landed = CollectLandedStandingKeys();
        _standingViews = items
            .Where(i => i.Enabled
                && !string.IsNullOrWhiteSpace(i.Subject)
                && !string.IsNullOrWhiteSpace(i.Content))
            .Select(i =>
            {
                var view = new StandingHomeworkView { Item = i };
                if (landed.Contains(StandingHomeworkView.LandingKey(i.Subject, i.Content)))
                {
                    // 今天已落档：勾选态与文档事实一致（已计入），且不可取消
                    //（取消也无法把它从当天文档里移除——旧实现一律重置为未勾选，与事实不符）
                    view.IsLanded = true;
                    view.IsChecked = true;
                }

                return view;
            })
            .ToList();
        StandingList.ItemsSource = _standingViews;
        StandingList.IsVisible = _standingViews.Count > 0;
        StandingSection.IsVisible = _standingViews.Count > 0;
    }

    /// <summary>当天文档里已落档的常态化条目键集合（学科 + 归一化内容）。</summary>
    private HashSet<string> CollectLandedStandingKeys() => CollectLandedStandingKeys(_documents);

    /// <summary>已落档的常态化条目键集合（纯函数，可单测）：只统计 IsStanding 条目。</summary>
    internal static HashSet<string> CollectLandedStandingKeys(IEnumerable<HomeworkDocument> documents)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var entry in document.Entries.Where(e => e.IsStanding))
            {
                keys.Add(StandingHomeworkView.LandingKey(document.Subject, entry.Text));
            }
        }

        return keys;
    }

    private void OnStandingItemToggled(object? sender, RoutedEventArgs e) => RefreshSendPreview();

    /// <summary>勾选变化后刷新待发文本与发送按钮（XAML 事件与测试共用同一路径）。</summary>
    internal void RefreshSendPreview()
    {
        // 勾选即时刷新清单（取消勾选即从清单移除）；落档仍只在点「发送」时发生。
        // 老师已手动编辑过待发消息时不覆盖其文本——需要重建时点「重新生成清单」。
        if (SendPreviewEdited)
        {
            SendConfirmButton.IsEnabled = CanSendPreview();
            return;
        }

        ApplyGeneratedPreview();
    }

    /// <summary>按当前文档与勾选项生成清单文本（自动补填序号按连接设置 NumberDigestLines）。</summary>
    private string BuildDigest()
    {
        var checkedItems = _standingViews.Where(v => v.IsChecked).Select(v => v.Item).ToList();
        if (_documents.Count == 0 && checkedItems.Count == 0)
        {
            return EmptyDigestText;
        }

        return HomeworkDigestFormatter.FormatDocuments(_documents, checkedItems, NumberDigestLines());
    }

    /// <summary>「整理并发送：自动补填序号」开关（连接设置 NumberDigestLines，缺省开）。</summary>
    private bool NumberDigestLines()
    {
        try
        {
            return _settingsService?.Current.Connection.NumberDigestLines ?? true;
        }
        catch
        {
            return true; // 设置读取失败按默认（开）处理，不影响发送
        }
    }

    private void OnSendCancelClick(object? sender, RoutedEventArgs e) => HideSendOverlay();

    /// <summary>关闭确认浮层（取消、发送完成自动收起；测试也用它验证编辑态恢复）。</summary>
    internal void HideSendOverlay()
    {
        SendConfirmOverlay.IsVisible = false;
        SendResultText.IsVisible = false;
        SendResultText.Text = "";
        // 退出待发编辑：恢复「不抢焦点、钉在桌面层」契约（文档编辑态仍在进行时不动它）
        if (_editing is null)
        {
            _overlays?.SetOverlayEditing(SuspensionWindowController.HomeworkKey, editing: false);
        }
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
            var (summary, allSucceeded) = await ConfirmSendAsync();
            SendResultText.Text = summary;
            if (allSucceeded)
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
    /// 执行一次发送（按钮与测试共用同一入口），返回（结果摘要, 是否全部成功）。
    /// <para>
    /// 顺序不可颠倒：<b>先</b>取用户看到的清单文本（含勾选的常态化作业行），<b>再</b>落档常态化作业。
    /// 落档后再格式化会把这些行再追加一遍——曾经的「发送内容里常态化作业重复」根因。
    /// </para>
    /// </summary>
    internal async Task<(string Summary, bool AllSucceeded)> ConfirmSendAsync()
    {
        if (_sendService is null)
        {
            return ("发送失败：整理并发送未启用", false);
        }

        if (!CanSendPreview())
        {
            return ("发送失败：待发内容为空（今天还没有作业，或待发消息被清空）", false);
        }

        // 待发即所得：发送的就是确认窗里那段文本（老师可能已手动修改）。
        // 顺序不可颠倒：先取文本、再落档常态化作业（落档后重新格式化会再追加一遍）。
        var digest = SendPreview;

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
        return (string.Join(Environment.NewLine, lines), results.Count > 0 && okCount == results.Count);
    }

    /// <summary>
    /// 把勾选的常态化作业写入对应学科文档末尾（IsStanding 条目，来源元信息标为常态化）。
    /// 返回 null 表示全部成功，否则返回可展示的失败摘要（不阻断发送）。
    /// </summary>
    private async Task<string?> ApplyStandingHomeworkAsync()
    {
        // 已落档的条目跳过：它今天已在文档里（重复追加会被合并器判为完全重复而丢弃），
        // 只对本次新勾选的条目落档。
        var checkedItems = _standingViews
            .Where(v => v.IsChecked && !v.IsLanded)
            .Select(v => v.Item)
            .ToList();
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
