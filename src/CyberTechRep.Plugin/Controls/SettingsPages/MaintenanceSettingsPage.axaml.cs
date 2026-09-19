using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.FirstRun;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 维护设置页：日志级别、失败重试队列查看/重放（联动模块 8）、排错面板
/// （连接状态/最近消息快照/手动重连，联动 <see cref="IDiagnosticsService"/>）、
/// 消息日志 dump 导出（联动 <see cref="IMessageDumpService"/>）、
/// 配置导入导出（剪贴板通道，导出脱敏/导入保留本地 Secret）/恢复默认，
/// 以及 NapCat 后台日志实时视图（stdout/stderr 环形缓冲 300ms 批量刷新，联动
/// <see cref="NapCatRunnerService"/>）。
/// </summary>
[SettingsPageInfo("cybertechrep.settings.maintenance", "CyberTechRep 维护")]
[Group("cybertechrep.settings")]
public partial class MaintenanceSettingsPage : CyberTechRepSettingsPageBase
{
    /// <summary>重试队列列表行（显示文本 + 条目 Id）。</summary>
    public sealed record RetryLine(string Text, Guid Id);

    private static readonly string[] LogLevelNames = ["Trace", "Debug", "Info", "Warning", "Error"];

    /// <summary>NapCat 日志刷新节拍：300ms 合并一次，避免逐行渲染拖慢 UI 线程。</summary>
    private static readonly TimeSpan NapCatLogRefreshInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>日志服务未注入时的状态提示（DI 缺失，页面仍可用其余功能）。</summary>
    private const string NapCatLogNotWiredText =
        "后台日志暂不可用（当前环境没有提供日志服务）。";

    private readonly IDiagnosticsService? _diagnostics;
    private readonly IFirstRunService? _firstRun;
    private readonly IMessageDumpService? _messageDump;
    private readonly NapCatRunnerService? _napCatRunner;
    private readonly DispatcherTimer? _napCatLogTimer;

    private string _diagnosticsSummaryText = "还没加载，点「刷新」查看。";
    private IReadOnlyList<string> _recentMessages = [];
    private IReadOnlyList<RetryLine> _retryQueueLines = [];
    private RetryLine? _selectedRetryLine;
    private string _transferFeedback = "";
    private string _dumpFeedback = "";
    private string _napCatStatusText;
    private string _napCatLogText = "";
    private string _napCatDroppedText = "";
    private bool _napCatLogPaused;
    private long _lastRenderedSequence = -1;
    private long _lastRenderedDropped = -1;
    private bool _scrollToEndPending;

    public MaintenanceSettingsPage(ISettingsService settingsService, IDiagnosticsService? diagnostics = null,
        IFirstRunService? firstRun = null, IMessageDumpService? messageDump = null,
        NapCatRunnerService? napCatRunner = null)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        _diagnostics = diagnostics;
        _firstRun = firstRun;
        _messageDump = messageDump;
        _napCatRunner = napCatRunner;
        _napCatStatusText = napCatRunner is null
            ? NapCatLogNotWiredText
            : FormatNapCatStatus(napCatRunner.Status);
        if (napCatRunner is not null)
        {
            _napCatLogTimer = new DispatcherTimer { Interval = NapCatLogRefreshInterval };
            _napCatLogTimer.Tick += OnNapCatLogTimerTick;
        }

        InitializeComponent();

        // 定时器随页面可见性启停：设置页关闭后不再轮询，避免定时器泄漏
        AttachedToVisualTree += OnNapCatLogAttached;
        DetachedFromVisualTree += OnNapCatLogDetached;
    }

    // ============ 日志级别（索引 ↔ 名称映射） ============

    public int LogLevelIndex
    {
        get
        {
            var idx = Array.IndexOf(LogLevelNames, Settings.Maintenance.LogLevel);
            return idx < 0 ? 2 : idx;
        }
        set
        {
            if (value >= 0 && value < LogLevelNames.Length)
            {
                Settings.Maintenance.LogLevel = LogLevelNames[value];
            }
        }
    }

    // ============ 排错面板 ============

    public string DiagnosticsSummaryText
    {
        get => _diagnosticsSummaryText;
        private set
        {
            _diagnosticsSummaryText = value;
            RaisePropertyChanged(nameof(DiagnosticsSummaryText));
        }
    }

    public IReadOnlyList<string> RecentMessages
    {
        get => _recentMessages;
        private set
        {
            _recentMessages = value;
            RaisePropertyChanged(nameof(RecentMessages));
        }
    }

    /// <summary>消息日志 dump 导出反馈（成功时含导出路径与行数）。</summary>
    public string DumpFeedback
    {
        get => _dumpFeedback;
        private set
        {
            _dumpFeedback = value;
            RaisePropertyChanged(nameof(DumpFeedback));
        }
    }

    public IReadOnlyList<RetryLine> RetryQueueLines
    {
        get => _retryQueueLines;
        private set
        {
            _retryQueueLines = value;
            RaisePropertyChanged(nameof(RetryQueueLines));
        }
    }

    /// <summary>重试队列列表当前选中行（重放目标）。</summary>
    public RetryLine? SelectedRetryLine
    {
        get => _selectedRetryLine;
        set
        {
            _selectedRetryLine = value;
            RaisePropertyChanged(nameof(SelectedRetryLine));
        }
    }

    // ============ NapCat 后台日志（环形缓冲 300ms 批量刷新） ============

    /// <summary>NapCat 运行状态行（状态 + 详情）；未运行/已停止时给出明确启动提示。</summary>
    public string NapCatStatusText
    {
        get => _napCatStatusText;
        private set
        {
            if (_napCatStatusText == value)
            {
                return;
            }

            _napCatStatusText = value;
            RaisePropertyChanged(nameof(NapCatStatusText));
        }
    }

    /// <summary>日志视图文本（最近一次快照的合并结果，只读可复制）。</summary>
    public string NapCatLogText
    {
        get => _napCatLogText;
        private set
        {
            if (_napCatLogText == value)
            {
                return;
            }

            _napCatLogText = value;
            RaisePropertyChanged(nameof(NapCatLogText));
        }
    }

    /// <summary>环形缓冲丢弃提示（DroppedCount &gt; 0 时显示，否则为空）。</summary>
    public string NapCatDroppedText
    {
        get => _napCatDroppedText;
        private set
        {
            if (_napCatDroppedText == value)
            {
                return;
            }

            _napCatDroppedText = value;
            RaisePropertyChanged(nameof(NapCatDroppedText));
        }
    }

    /// <summary>暂停滚动：暂停期间日志继续收集但不移动光标/滚动条；恢复后跳回末尾。</summary>
    public bool NapCatLogPaused
    {
        get => _napCatLogPaused;
        set
        {
            if (_napCatLogPaused == value)
            {
                return;
            }

            _napCatLogPaused = value;
            if (!value)
            {
                _scrollToEndPending = true;
            }

            RaisePropertyChanged(nameof(NapCatLogPaused));
        }
    }

    /// <summary>页面挂载：立即渲染一次并启动定时器（避免展开面板后 300ms 空窗）。</summary>
    private void OnNapCatLogAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        OnNapCatLogTimerTick(null, EventArgs.Empty);
        _napCatLogTimer?.Start();
    }

    /// <summary>页面卸载：停止定时器（不泄漏；日志缓冲仍由服务端继续收集）。</summary>
    private void OnNapCatLogDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _napCatLogTimer?.Stop();
    }

    /// <summary>
    /// 定时刷新：状态行每拍更新；日志仅在最新序号或丢弃计数变化时取快照并重排文本，
    /// 无变化时零分配零渲染。
    /// </summary>
    private void OnNapCatLogTimerTick(object? sender, EventArgs e)
    {
        var runner = _napCatRunner;
        if (runner is null)
        {
            return;
        }

        NapCatStatusText = FormatNapCatStatus(runner.Status);

        var buffer = runner.LogBuffer;
        var lastSequence = buffer.LastSequence;
        var dropped = buffer.DroppedCount;
        var changed = lastSequence != _lastRenderedSequence || dropped != _lastRenderedDropped;

        // 暂停滚动期间不替换绑定文本：TextBox 内容/光标/滚动位置完全冻结（视图“不动”），
        // 日志仍在环形缓冲里继续累积；恢复后由下一次定时刷新一次性补齐（序号基线未推进，
        // 因此恢复瞬间必然重排一次，不会漏行）。否则暂停查看时文本被重排，光标/滚动会被拽走。
        if (changed && !NapCatLogPaused)
        {
            var snapshot = buffer.Snapshot();
            var builder = new StringBuilder(snapshot.Count * 80);
            foreach (var line in snapshot)
            {
                builder.Append('[').Append(line.Timestamp.ToString("HH:mm:ss.fff")).Append("] ");
                builder.Append(line.Stream switch
                {
                    NapCatLogStream.StdOut => "OUT",
                    NapCatLogStream.StdErr => "ERR",
                    _ => "SYS"
                });
                builder.Append(' ').Append(line.Text).Append('\n');
            }

            _lastRenderedSequence = lastSequence;
            _lastRenderedDropped = dropped;
            NapCatLogText = builder.ToString();
            NapCatDroppedText = dropped > 0 ? $"有 {dropped} 行日志已被丢弃（超出显示上限）" : "";
        }

        if (NapCatLogPaused)
        {
            return; // 暂停：继续收集但不移动光标/滚动条（恢复时跳回末尾）
        }

        if (_scrollToEndPending || (changed && IsLogViewNearEnd()))
        {
            _scrollToEndPending = false;
            ScrollLogToEnd();
        }
    }

    /// <summary>清空日志：清空缓冲与视图，行序号基线对齐，丢弃计数归零。</summary>
    private void OnClearNapCatLogClicked(object? sender, RoutedEventArgs e)
    {
        var runner = _napCatRunner;
        if (runner is not null)
        {
            runner.LogBuffer.Clear();
            _lastRenderedSequence = runner.LogBuffer.LastSequence;
            _lastRenderedDropped = 0;
        }

        NapCatLogText = "";
        NapCatDroppedText = "";
        _scrollToEndPending = true;
    }

    /// <summary>视图是否已接近末尾（用户手动上滚阅读时不打断其位置）。</summary>
    private bool IsLogViewNearEnd()
    {
        var scrollViewer = FindLogScrollViewer();
        if (scrollViewer is null)
        {
            return true; // 模板尚未布局完成：视为在末尾
        }

        return scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 24;
    }

    /// <summary>光标移到末尾并在布局完成后滚动到底。</summary>
    private void ScrollLogToEnd()
    {
        var view = NapCatLogView;
        if (view is null)
        {
            return;
        }

        try
        {
            view.CaretIndex = NapCatLogText.Length;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    FindLogScrollViewer()?.ScrollToEnd();
                }
                catch
                {
                    // 视图已分离/模板重建：忽略
                }
            }, DispatcherPriority.Background);
        }
        catch
        {
            // 视图尚未就绪：忽略
        }
    }

    private ScrollViewer? FindLogScrollViewer()
        => NapCatLogView?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>状态行文本：按运行状态给出中文提示；未运行/已停止时附带启动入口指引。</summary>
    private static string FormatNapCatStatus(NapCatRunnerStatus status)
        => status.State switch
        {
            NapCatRunnerState.NotRunning =>
                "NapCat 未运行，暂无日志。可在「连接设置」的「NapCat 一键启动」里启动，或打开自动启动。",
            NapCatRunnerState.Starting =>
                $"NapCat 正在启动…（{status.Detail}）",
            NapCatRunnerState.Running =>
                $"NapCat 运行中（{status.Detail}）",
            NapCatRunnerState.Stopped =>
                $"NapCat 已停止（{status.Detail}）。可在「连接设置」的「NapCat 一键启动」里重新启动。",
            NapCatRunnerState.Failed =>
                $"NapCat 启动失败：{status.Detail}",
            _ => status.Detail
        };

    /// <summary>连接状态的中文显示文案（仅影响排错面板显示，不改变任何行为）。</summary>
    private static string DescribeConnectionStatus(ConnectionStatus status)
        => status switch
        {
            ConnectionStatus.Connected => "已连接",
            ConnectionStatus.Connecting => "正在连接",
            ConnectionStatus.Authenticating => "正在确认登录状态",
            ConnectionStatus.AuthenticationFailed => "登录被拒绝（请检查密钥或 token）",
            ConnectionStatus.Reconnecting => "正在重连",
            ConnectionStatus.Faulted => "连接异常",
            ConnectionStatus.Disconnected => "未连接",
            _ => status.ToString()
        };

    /// <summary>重试任务状态的中文显示文案（仅影响排错面板显示，不改变任何行为）。</summary>
    private static string DescribeRetryStatus(RetryItemStatus status)
        => status switch
        {
            RetryItemStatus.Waiting => "等待重试",
            RetryItemStatus.Retrying => "正在重试",
            RetryItemStatus.Succeeded => "已完成",
            RetryItemStatus.GivenUp => "已放弃",
            _ => status.ToString()
        };

    /// <summary>重试任务类型的中文显示文案（仅影响排错面板显示，不改变任何行为）。</summary>
    private static string DescribeRetryOperation(RetryOperationType operation)
        => operation switch
        {
            RetryOperationType.FileDownload => "下载文件",
            RetryOperationType.SubjectClassify => "识别学科",
            RetryOperationType.StoreWrite => "保存记录",
            _ => "其他任务"
        };

    // ============ 导入导出反馈 ============

    public string TransferFeedback
    {
        get => _transferFeedback;
        private set
        {
            _transferFeedback = value;
            RaisePropertyChanged(nameof(TransferFeedback));
        }
    }

    // ============ 事件处理 ============

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);

    private async void OnRefreshDiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        if (_diagnostics is null)
        {
            DiagnosticsSummaryText = "排错服务不可用。";
            return;
        }

        try
        {
            var snapshot = await _diagnostics.GetSnapshotAsync().ConfigureAwait(true);
            var offline = snapshot.OfflineDuration is { } d ? $"{d.TotalMinutes:F0} 分钟" : "在线";
            DiagnosticsSummaryText =
                $"连接状态：{DescribeConnectionStatus(snapshot.ConnectionStatus)}；QQ 消息通道离线时长：{offline}；" +
                $"磁盘剩余：{(snapshot.DiskFreeMb is { } mb ? $"{mb} MB" : "未知")}；" +
                $"待重试任务：{snapshot.RetryQueue.Count} 个";

            RecentMessages = snapshot.RecentMessages
                .Select(m => $"[{m.ReceivedAt:MM-dd HH:mm:ss}] 群 {m.GroupOpenId} {m.SenderNickname}：{m.Preview}")
                .ToList();

            RetryQueueLines = snapshot.RetryQueue
                .Select(i => new RetryLine(
                    $"{DescribeRetryStatus(i.Status)}｜{DescribeRetryOperation(i.OperationType)}｜已尝试 {i.AttemptCount} 次｜下次 {i.NextAttemptAt:HH:mm:ss}｜{i.LastError}",
                    i.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticsSummaryText = $"读取排错信息失败：{ex.Message}";
        }
    }

    private async void OnReconnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_diagnostics is null)
        {
            return;
        }

        try
        {
            await _diagnostics.ReconnectAsync().ConfigureAwait(true);
            TransferFeedback = "已重新连接。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"手动重连失败：{ex.Message}";
        }
    }

    private async void OnReplayClicked(object? sender, RoutedEventArgs e)
    {
        var line = SelectedRetryLine;
        if (line is null)
        {
            TransferFeedback = "请先在「待重试任务」列表里选中一条。";
            return;
        }

        if (_diagnostics is null)
        {
            TransferFeedback = "重试服务不可用，暂时无法重新执行。";
            return;
        }

        try
        {
            await _diagnostics.ReplayAsync(line.Id).ConfigureAwait(true);
            TransferFeedback = "已重新执行选中的任务。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"重新执行失败：{ex.Message}";
        }
    }

    private async void OnExportDumpClicked(object? sender, RoutedEventArgs e)
    {
        if (_messageDump is null)
        {
            DumpFeedback = "消息记录导出暂不可用。";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            DumpFeedback = "无法获取窗口，导出已取消。";
            return;
        }

        string? filePath = null;
        try
        {
            // 用户可选保存路径；选择器不可用时兜底导出到桌面
            var picker = topLevel.StorageProvider;
            var file = await picker.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出消息记录",
                SuggestedFileName = $"CyberTechRep-消息记录-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultExtension = "jsonl",
                FileTypeChoices = [new FilePickerFileType("JSONL 文本文件（每行一条消息）")
                {
                    Patterns = ["*.jsonl"]
                }]
            }).ConfigureAwait(true);
            filePath = file?.Path.LocalPath;
        }
        catch
        {
            // 选择器异常（部分宿主环境不可用）→ 回退桌面路径，不阻断导出
        }

        try
        {
            filePath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"CyberTechRep-消息记录-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

            var count = await _messageDump.ExportAsync(filePath).ConfigureAwait(true);
            DumpFeedback = count == 0
                ? $"还没收到消息，已导出空文件：{filePath}"
                : $"已导出 {count} 条消息 → {filePath}";
        }
        catch (Exception ex)
        {
            DumpFeedback = $"导出失败：{ex.Message}";
        }
    }

    private async void OnReopenWizardClicked(object? sender, RoutedEventArgs e)
    {
        if (_firstRun is null)
        {
            TransferFeedback = "引导窗口暂不可用。";
            return;
        }

        try
        {
            var shown = await _firstRun.ShowWizardAsync().ConfigureAwait(true);
            TransferFeedback = shown ? "已打开首次启动引导。" : "打开引导窗口失败，详情见插件日志。";
        }
        catch (Exception ex)
        {
            // 引导服务本身不外抛；此处兜底保证设置页不崩溃
            TransferFeedback = $"打开引导失败：{ex.Message}";
        }
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var json = await SettingsService.ExportAsync().ConfigureAwait(true);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                TransferFeedback = "剪贴板不可用。";
                return;
            }

            await clipboard.SetTextAsync(json).ConfigureAwait(true);
            TransferFeedback = "已把设置复制到剪贴板（不含密钥等敏感信息）。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"导出失败：{ex.Message}";
        }
    }

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                TransferFeedback = "剪贴板不可用。";
                return;
            }

            var json = await clipboard.GetTextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json))
            {
                TransferFeedback = "剪贴板里没有可导入的设置内容。";
                return;
            }

            await SettingsService.ImportAsync(json).ConfigureAwait(true);
            TransferFeedback = "导入成功；本机已保存的密钥类信息保持不变。";
        }
        catch (FormatException ex)
        {
            TransferFeedback = $"导入失败，内容格式不正确：{ex.Message}";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"导入失败：{ex.Message}";
        }
    }

    private async void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SettingsService.ResetToDefaultsAsync().ConfigureAwait(true);
            TransferFeedback = "已恢复默认设置。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"恢复默认失败：{ex.Message}";
        }
    }
}
