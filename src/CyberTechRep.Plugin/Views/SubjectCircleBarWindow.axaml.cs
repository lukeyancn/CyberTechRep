using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Views;

/// <summary>
/// 学科圆圈启动器（小型常驻窗，Avalonia 无边框半透明圆角）：
/// 圆圈内显示学科名（集合 = 固定七学科 ∪ files.json 里出现过的学科，顺序按
/// <see cref="SubjectCircleBarSettings.Order"/>，未配置的按固定顺序追加）；
/// 横竖排列由 Orientation 驱动；点击圆圈 → 经 <see cref="SubjectFilesController"/>
/// 打开/切换/隐藏（toggle）学科文件悬浮窗；右键圆圈弹出「上移/下移」菜单调整顺序
/// （持久化回设置并热生效）；底部控件条：「⋯」快捷菜单（与其他悬浮窗共用
/// <see cref="OverlayQuickMenu"/>：置顶/固定/穿透）+「×」隐藏（设置页可再唤出）；
/// 层级/穿透/固定/位置由 <see cref="SuspensionWindowController"/>
/// 钉底器路径统一处理（与另三窗行为一致）。
/// <para>
/// 拖放导入：从资源管理器等拖文件悬停到学科圆圈上松开 → 文件复制归档到该学科
/// （Copy 语义，经 <see cref="IFileImportService"/> 复用文件管道归档格式）。悬停时高亮
/// 目标圆圈提示可复制；负载不含文件路径时效果为 None。注意：窗口被钉底器打上
/// WS_EX_TRANSPARENT（鼠标穿透开 + 固定开）后收不到拖放事件——拖放仅在穿透关闭时可用。
/// </para>
/// </summary>
public partial class SubjectCircleBarWindow : Window
{
    private readonly SubjectFilesController _controller = null!;
    private readonly IFilePipelineService _pipeline = null!;
    private readonly Func<SubjectCircleBarSettings> _getSettings = null!;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger _logger;
    private readonly OverlayQuickMenu _quickMenu = null!;
    private EventHandler<CyberTechRep.Shared.Models.AppSettings>? _settingsChangedHandler;
    private string? _pendingSubject;

    /// <summary>拖放导入实现（管道同实例；测试替身管道可能未实现 → null 时拖放降级为提示）。</summary>
    private readonly IFileImportService? _importer;

    /// <summary>DragOver 高亮中的圆圈按钮（离开/放下/换目标时清除高亮）。</summary>
    private Button? _dropHighlight;

    /// <summary>拖放结果瞬态提示的自动隐藏计时器。</summary>
    private readonly DispatcherTimer _dropNoticeTimer = null!;

    public SubjectCircleBarWindow()
    {
        // 设计时/XAML 预览用；运行时走含依赖构造
        InitializeComponent();
    }

    public SubjectCircleBarWindow(
        SubjectFilesController controller,
        IFilePipelineService pipeline,
        Func<SubjectCircleBarSettings> getSettings,
        ILogger? logger = null,
        ISettingsService? settingsService = null,
        ISuspensionWindowController? overlays = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _settingsService = settingsService;
        _logger = logger ?? NullLogger.Instance;
        InitializeComponent();
        // 底部「⋯」快捷菜单（与其他悬浮窗共用 OverlayQuickMenu：置顶/固定/穿透，即时生效并
        // 回写设置；控件在底部 → 菜单向上展开）
        _quickMenu = new OverlayQuickMenu(
            settingsService, overlays, SuspensionWindowController.CircleKey,
            () => settingsService!.Current.Overlays.Circle, this, openAbove: true);
        QuickMenuButton.Flyout = _quickMenu.Flyout;
        _ = RefreshAsync();

        // 拖放导入：窗口级 AllowDrop + 路由事件（DragOver 命中学科圆圈 → Copy + 高亮；
        // Drop 提取路径后线程池异步复制归档，不冻结 UI）。非文件负载 → None。
        _importer = pipeline as IFileImportService;
        _dropNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _dropNoticeTimer.Tick += (_, _) =>
        {
            _dropNoticeTimer.Stop();
            DropStatusHost.IsVisible = false;
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave);
        AddHandler(DragDrop.DropEvent, OnFileDrop);

        // 可视树加载完成后再补一次方向应用：构造期 RefreshAsync 时 ItemsPanelRoot 可能尚未
        // 物化，ApplyOrientation 会静默跳过，导致初始设置的横/竖排列不生效。
        Opened += (_, _) => ApplyOrientation(TryGetSettings());

        // 设置页修改排列方向/学科顺序/视图模式并保存后，热刷新圆圈栏展示（UI 线程调度）
        if (_settingsService is not null)
        {
            _settingsChangedHandler = (_, _) => Dispatcher.UIThread.Post(() => { _ = RefreshAsync(); });
            _settingsService.SettingsChanged += _settingsChangedHandler;
            Closed += (_, _) => _settingsService.SettingsChanged -= _settingsChangedHandler;
        }
    }

    /// <summary>当前展示顺序（测试用）。</summary>
    public System.Collections.IEnumerable? VisibleSubjects => CircleList?.ItemsSource;

    internal async Task RefreshAsync()
    {
        try
        {
            var records = await _pipeline.GetRecordsAsync();
            var discovered = records
                .Where(r => r.Status == FileStatus.Archived)
                .Select(r => SubjectFilesQuery.ExtractSubject(r.ArchivedRelativePath))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal);

            var settings = TryGetSettings();
            CircleList.ItemsSource = SubjectCircleOrder.ResolveOrder(settings?.Order, discovered);
            ApplyOrientation(settings);
        }
        catch (Exception ex)
        {
            // 刷新失败保持上次内容，不阻断圆圈栏
            _logger.LogWarning(ex, "学科圆圈栏刷新失败（保持上次内容）");
        }
    }

    private SubjectCircleBarSettings? TryGetSettings()
    {
        try
        {
            return _getSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取圆圈栏设置失败，按默认方向/顺序显示");
            return null;
        }
    }

    /// <summary>横竖排列：切换 ItemsPanel 的 StackPanel 方向（窗口宽高可在设置页调整适配）。</summary>
    private void ApplyOrientation(SubjectCircleBarSettings? settings)
    {
        if (CircleList.ItemsPanelRoot is StackPanel panel)
        {
            panel.Orientation = SubjectCircleOrder.IsVertical(settings?.Orientation)
                ? Avalonia.Layout.Orientation.Vertical
                : Avalonia.Layout.Orientation.Horizontal;
        }
    }

    private async void OnCircleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string subject })
        {
            await _controller.ToggleOrSwitchAsync(subject);
        }
    }

    /// <summary>
    /// 空白处按住拖动整个圆圈栏（BeginMoveDrag，与另三窗标题栏同款）。
    /// 缺陷 c 修复：此前圆圈栏完全没有拖拽处理器，窗口位置只能经设置页 X/Y 调整。
    /// 按在圆圈按钮/右键菜单上时不拖拽（保留点击与菜单交互）；固定模式下禁用（门控与另窗一致）。
    /// </summary>
    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 固定模式下禁用拖拽（位置只能经设置页调整）
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        // 按在圆圈按钮（或其内部元素）上时交给按钮的 Click/ContextMenu，不启动拖拽
        if (IsOverInteractive(e.Source as Visual))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>指针源是否位于可交互控件（Button 等）内：沿可视树向上找。</summary>
    private static bool IsOverInteractive(Visual? source)
    {
        for (Visual? node = source; node is not null; node = node.GetVisualParent())
        {
            if (node is Button)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>右键菜单展开时记录所属圆圈的学科（MenuItem 不在可视树内，经 ContextMenu 的 PlacementTarget 取）。</summary>
    private void OnMenuOpened(object? sender, RoutedEventArgs e)
    {
        _pendingSubject = sender is ContextMenu { PlacementTarget: Button { DataContext: string subject } }
            ? subject
            : null;
    }

    private async void OnMoveUpClick(object? sender, RoutedEventArgs e) => await MoveAsync(-1);

    private async void OnMoveDownClick(object? sender, RoutedEventArgs e) => await MoveAsync(1);

    /// <summary>底部「×」：隐藏圆圈栏（Visible=false 由控制器可见性钩子同步回设置，设置页可再唤出）。</summary>
    private void OnHideClick(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>上移/下移：以当前展示顺序为基准重排，持久化后刷新（顺序在设置间共享）。</summary>
    private async Task MoveAsync(int delta)
    {
        try
        {
            if (_pendingSubject is { } subject
                && CircleList.ItemsSource is IReadOnlyList<string> displayed)
            {
                _pendingSubject = null;
                await _controller.ReorderAsync(displayed, subject, delta);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "圆圈顺序调整失败（已吞掉）");
        }
    }

    // ============ 拖放导入（文件 → 学科，Copy 语义）============

    /// <summary>沿可视树向上找到拖放命中点的学科圆圈按钮（DataContext 为学科名字符串）。</summary>
    private static Button? FindSubjectButton(Visual? source)
    {
        for (Visual? node = source; node is not null; node = node.GetVisualParent())
        {
            if (node is Button { DataContext: string } button)
            {
                return button;
            }
        }

        return null;
    }

    private void ClearDropHighlight()
    {
        _dropHighlight?.Classes.Remove("drop-target");
        _dropHighlight = null;
    }

    /// <summary>
    /// DragOver：悬停在学科圆圈上且负载含文件路径 → 显式 Copy + 高亮该圆圈；
    /// 空白处 / 非文件负载 / 导入实现缺失 → None（不可放置）。
    /// </summary>
    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        var button = FindSubjectButton(e.Source as Visual);
        if (_dropHighlight != button)
        {
            ClearDropHighlight();
            button?.Classes.Add("drop-target");
            _dropHighlight = button;
        }

        if (button is null || _importer is null || !SubjectDropImport.HasFiles(e.Data))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
    }

    /// <summary>拖放离开窗口/圆圈：清除高亮（视觉提示复位）。</summary>
    private void OnFileDragLeave(object? sender, DragEventArgs e) => ClearDropHighlight();

    /// <summary>
    /// Drop：提取全部文件路径后在线程池逐个复制归档到目标学科（大批量不冻结 UI），
    /// 结果汇总为瞬态提示；源文件保持原位（Copy 语义在 <see cref="IFileImportService"/> 内保证）。
    /// 非文件负载/无路径 → 非致命提示，不抛异常。
    /// </summary>
    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        try
        {
            e.Handled = true;
            var button = FindSubjectButton(e.Source as Visual);
            ClearDropHighlight();

            if (button?.DataContext is not string subject)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Copy;
            var paths = SubjectDropImport.ExtractFilePaths(e.Data);
            if (_importer is null)
            {
                ShowDropNotice("当前文件管道不支持拖放导入");
                return;
            }

            if (paths.Count == 0)
            {
                // 非文件系统拖放（压缩包虚拟文件/纯文本等）且无法提取路径：非致命提示
                ShowDropNotice("未识别到可导入的文件路径（非文件拖放或虚拟文件），已忽略");
                return;
            }

            ShowDropNotice($"正在复制 {paths.Count} 个文件到「{subject}」…");
            var importer = _importer;
            var results = await Task.Run(() => SubjectDropImport.ImportAllAsync(importer, paths, subject));

            var imported = 0;
            var duplicated = 0;
            var skipped = 0;
            var failed = new List<string>();
            foreach (var (_, result) in results)
            {
                switch (result.Outcome)
                {
                    case SubjectDropImportOutcome.Imported:
                        imported++;
                        break;
                    case SubjectDropImportOutcome.Duplicate:
                        duplicated++;
                        break;
                    case SubjectDropImportOutcome.Skipped:
                        skipped++;
                        break;
                    default:
                        failed.Add(result.Message);
                        break;
                }
            }

            var summary = $"已复制 {imported} 个文件到「{subject}」";
            if (duplicated > 0)
            {
                summary += $"，{duplicated} 个内容重复跳过";
            }

            if (skipped > 0)
            {
                summary += $"，{skipped} 个跳过";
            }

            foreach (var message in failed)
            {
                _logger.LogWarning("拖放导入失败：{Message}", message);
            }

            if (failed.Count > 0)
            {
                summary += $"，{failed.Count} 个失败（详见日志）";
            }

            _logger.LogInformation("拖放导入完成：{Summary}", summary);
            ShowDropNotice(summary);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // 拖放处理任何异常都不打断圆圈栏（非致命：提示 + 日志）
            _logger.LogWarning(ex, "拖放导入处理异常（已吞掉）");
            ShowDropNotice("拖放导入失败（详见日志）");
        }
    }

    /// <summary>显示瞬态提示文本（几秒后自动隐藏；重复调用重置计时）。</summary>
    private void ShowDropNotice(string message)
    {
        DropStatusText.Text = message;
        DropStatusHost.IsVisible = true;
        _dropNoticeTimer.Stop();
        _dropNoticeTimer.Start();
    }
}
