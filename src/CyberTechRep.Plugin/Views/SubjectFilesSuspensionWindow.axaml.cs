using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Views;

/// <summary>学科文件悬浮窗条目视图（记录 + 关联图标）。</summary>
public sealed class SubjectFileItemView
{
    public required FileRecord Record { get; init; }

    public string FileName => Record.FileName;

    /// <summary>文件关联图标（提取失败为 null，XAML 用通用图标兜底）。</summary>
    public Bitmap? Icon { get; init; }

    public bool HasIcon => Icon is not null;
}

/// <summary>学科文件悬浮窗日期分组视图（组头为 yyyy-MM-dd）。</summary>
public sealed class SubjectFileGroupView
{
    public required string Date { get; init; }

    public required IReadOnlyList<SubjectFileItemView> Items { get; init; }
}

/// <summary>
/// 学科文件悬浮窗（第三悬浮窗，Avalonia 无边框窗）：
/// 展示指定学科已归档文件（Status==Archived，按 ArchivedRelativePath 学科段过滤），
/// 按归档日期分组（日期为组头，文件按时间倒序）；两种视图模式——大图标（WrapPanel 流式）
/// / 详细列表（小图标在左、完整文件名在右），模式由圆圈栏设置记忆、点击文件用系统默认
/// 程序打开；SetSubject 原地切换学科（供圆圈栏 toggle/切换交互复用窗口实例，不闪关）；
/// 位置/大小/层级由 <see cref="SuspensionWindowController"/> 持久化与应用。
/// <para>
/// 拖放导入：拖文件到悬浮窗松开 → 复制归档到当前展示学科（Copy 语义，经
/// <see cref="IFileImportService"/> 复用文件管道归档格式，源文件保留原位）。拖入时高亮
/// 窗口外框提示可复制；负载不含文件路径时给出非致命提示。注意：窗口被钉底器打上
/// WS_EX_TRANSPARENT（鼠标穿透开 + 固定开）后收不到拖放事件——拖放仅在穿透关闭时可用。
/// </para>
/// </summary>
public partial class SubjectFilesSuspensionWindow : Window
{
    /// <summary>FileUpdated 事件合并刷新的 debounce 间隔（与通知/作业悬浮窗一致）。</summary>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IFilePipelineService _pipeline = null!;
    private readonly Func<SubjectCircleBarSettings> _getCircleSettings = null!;
    private readonly Func<string, string?> _resolveAbsolutePath = null!;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _debounceTimer = null!;
    private readonly OverlayQuickMenu _quickMenu = null!;
    private int _refreshing;
    private string _subject = "未分类";

    /// <summary>拖放导入实现（管道同实例；测试替身管道可能未实现 → null 时拖放降级为提示）。</summary>
    private readonly IFileImportService? _importer;

    /// <summary>拖放结果瞬态提示的自动隐藏计时器。</summary>
    private readonly DispatcherTimer _dropNoticeTimer = null!;

    public SubjectFilesSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含依赖构造
        InitializeComponent();
    }

    public SubjectFilesSuspensionWindow(
        IFilePipelineService pipeline,
        Func<SubjectCircleBarSettings> getCircleSettings,
        Func<string, string?> resolveAbsolutePath,
        ILogger? logger = null,
        ISuspensionWindowController? overlays = null,
        ISettingsService? settingsService = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _getCircleSettings = getCircleSettings ?? throw new ArgumentNullException(nameof(getCircleSettings));
        _resolveAbsolutePath = resolveAbsolutePath ?? throw new ArgumentNullException(nameof(resolveAbsolutePath));
        _logger = logger ?? NullLogger.Instance;
        InitializeComponent();
        // 右上角「⋯」快捷菜单（与通知窗共用 OverlayQuickMenu：置顶/固定/穿透，即时生效并回写设置）
        _quickMenu = new OverlayQuickMenu(
            settingsService, overlays, SuspensionWindowController.FilesKey,
            () => settingsService!.Current.Overlays.Files, this);
        QuickMenuButton.Flyout = _quickMenu.Flyout;
        _debounceTimer = new DispatcherTimer { Interval = RefreshDebounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RefreshAsync();
        };
        _pipeline.FileUpdated += OnPipelineFileUpdated;
        _ = RefreshAsync();

        // 拖放导入：窗口级 AllowDrop + 路由事件（DragOver 含文件路径 → Copy + 外框高亮；
        // Drop 提取路径后线程池异步复制归档到当前学科，不冻结 UI）。非文件负载 → 非致命提示。
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
    }

    /// <summary>当前展示的学科（圆圈栏 toggle 判断依据）。</summary>
    public string CurrentSubject => _subject;

    /// <summary>当前列表数据源（测试用）。</summary>
    public System.Collections.IEnumerable? VisibleGroups => IconsGroups?.ItemsSource;

    /// <summary>
    /// 原地切换学科（内容刷新，不重建窗口——圆圈栏切换不闪关）。
    /// 同学科重复调用为幂等 no-op。
    /// </summary>
    public void SetSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)
            || string.Equals(_subject, subject, StringComparison.Ordinal))
        {
            return;
        }

        _subject = subject;
        _ = RefreshAsync();
    }

    /// <summary>按设置应用视图模式（大图标/详细列表；设置变更后由控制器同步调用）。</summary>
    public void ApplyViewMode()
    {
        var details = SubjectCircleOrder.IsDetailsView(TryGetCircleSettings()?.ViewMode);
        IconsHost.IsVisible = !details;
        DetailsHost.IsVisible = details;
    }

    private SubjectCircleBarSettings? TryGetCircleSettings()
    {
        try
        {
            return _getCircleSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取圆圈栏设置失败，按默认视图显示");
            return null;
        }
    }

    private void OnPipelineFileUpdated(object? sender, FileRecord e)
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
            var records = await _pipeline.GetRecordsAsync();
            var filtered = SubjectFilesQuery.FilterBySubject(records, _subject);
            var groups = SubjectFilesQuery.GroupByDate(filtered)
                .Select(g => new SubjectFileGroupView
                {
                    Date = g.Date,
                    Items = g.Items.Select(ToItemView).ToList()
                })
                .ToList();

            SubjectTitle.Text = _subject;
            EmptyText.Text = $"「{_subject}」暂无已归档文件";
            EmptyText.IsVisible = groups.Count == 0;
            IconsGroups.ItemsSource = groups;
            DetailsGroups.ItemsSource = groups;
            ApplyViewMode();
        }
        catch (Exception ex)
        {
            // 刷新失败保持上次内容，不阻断悬浮窗
            _logger.LogWarning(ex, "学科文件悬浮窗刷新失败（保持上次内容）Subject={Subject}", _subject);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private SubjectFileItemView ToItemView(FileRecord record)
    {
        Bitmap? icon = null;
        var path = record.ArchivedRelativePath is { } relative ? _resolveAbsolutePath(relative) : null;
        if (path is not null && FileIconProvider.TryGetIconPng(path, record.FileName, out var png))
        {
            try
            {
                icon = new Bitmap(new MemoryStream(png));
            }
            catch
            {
                icon = null;
            }
        }

        return new SubjectFileItemView { Record = record, Icon = icon };
    }

    private void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubjectFileItemView item }
            || item.Record.ArchivedRelativePath is not { } relative)
        {
            return;
        }

        var path = _resolveAbsolutePath(relative);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _logger.LogWarning("文件不存在或路径解析失败，无法打开：{Relative}", relative);
            return;
        }

        // 仅 Windows：系统默认程序打开；失败吞异常记日志
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用系统默认程序打开文件失败：{Path}", path);
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

    // ============ 拖放导入（文件 → 当前学科，Copy 语义）============

    /// <summary>
    /// DragOver：负载含文件路径且导入实现可用 → 显式 Copy + 高亮窗口外框；否则 None。
    /// 目标学科 = 届时 <see cref="_subject"/>（Drop 时取实时值，切换学科后立即生效）。
    /// </summary>
    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        var canCopy = _importer is not null
            && !string.IsNullOrWhiteSpace(_subject)
            && SubjectDropImport.HasFiles(e.Data);
        if (!canCopy)
        {
            e.DragEffects = DragDropEffects.None;
            RootBorder.Classes.Remove("drop-target");
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        RootBorder.Classes.Add("drop-target");
    }

    /// <summary>拖放离开窗口：清除外框高亮（视觉提示复位）。</summary>
    private void OnFileDragLeave(object? sender, DragEventArgs e) => RootBorder.Classes.Remove("drop-target");

    /// <summary>
    /// Drop：提取全部文件路径后在线程池逐个复制归档到当前学科（大批量不冻结 UI），
    /// 结果汇总为瞬态提示并触发刷新（FileUpdated 事件也会带动圆圈栏数据源）。
    /// 非文件负载/无路径 → 非致命提示，不抛异常。
    /// </summary>
    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        try
        {
            e.Handled = true;
            RootBorder.Classes.Remove("drop-target");

            var subject = _subject;
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
            // 拖放处理任何异常都不打断悬浮窗（非致命：提示 + 日志）
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
