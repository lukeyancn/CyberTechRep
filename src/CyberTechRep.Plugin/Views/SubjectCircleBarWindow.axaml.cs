using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
}
