using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIng.Plugin.Services.Overlays;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Views;

/// <summary>
/// 学科圆圈启动器（小型常驻窗，Avalonia 无边框半透明圆角）：
/// 圆圈内显示学科名（集合 = 固定七学科 ∪ files.json 里出现过的学科，顺序按
/// <see cref="SubjectCircleBarSettings.Order"/>，未配置的按固定顺序追加）；
/// 横竖排列由 Orientation 驱动；点击圆圈 → 经 <see cref="SubjectFilesController"/>
/// 打开/切换/隐藏（toggle）学科文件悬浮窗；右键圆圈弹出「上移/下移」菜单调整顺序
/// （持久化回设置并热生效）；层级/穿透/固定/位置由 <see cref="SuspensionWindowController"/>
/// 钉底器路径统一处理（与另两窗行为一致）。
/// </summary>
public partial class SubjectCircleBarWindow : Window
{
    private readonly SubjectFilesController _controller = null!;
    private readonly IFilePipelineService _pipeline = null!;
    private readonly Func<SubjectCircleBarSettings> _getSettings = null!;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger _logger;
    private EventHandler<ClassIng.Shared.Models.AppSettings>? _settingsChangedHandler;
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
        ISettingsService? settingsService = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _settingsService = settingsService;
        _logger = logger ?? NullLogger.Instance;
        InitializeComponent();
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

    /// <summary>右键菜单展开时记录所属圆圈的学科（MenuItem 不在可视树内，经 ContextMenu 的 PlacementTarget 取）。</summary>
    private void OnMenuOpened(object? sender, RoutedEventArgs e)
    {
        _pendingSubject = sender is ContextMenu { PlacementTarget: Button { DataContext: string subject } }
            ? subject
            : null;
    }

    private async void OnMoveUpClick(object? sender, RoutedEventArgs e) => await MoveAsync(-1);

    private async void OnMoveDownClick(object? sender, RoutedEventArgs e) => await MoveAsync(1);

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
