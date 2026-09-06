using System.ComponentModel;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIng.Plugin.Services.FirstRun;
using ClassIng.Plugin.Services.Maintenance;
using ClassIng.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 维护设置页：日志级别、失败重试队列查看/重放（联动模块 8）、排错面板
/// （连接状态/最近消息快照/手动重连，联动 <see cref="IDiagnosticsService"/>）、
/// 消息日志 dump 导出（联动 <see cref="IMessageDumpService"/>）、
/// 配置导入导出（剪贴板通道，导出脱敏/导入保留本地 Secret）/恢复默认。
/// </summary>
[SettingsPageInfo("classing.settings.maintenance", "CyberTechRep 维护")]
[Group("classing.settings")]
public partial class MaintenanceSettingsPage : ClassIngSettingsPageBase
{
    /// <summary>重试队列列表行（显示文本 + 条目 Id）。</summary>
    public sealed record RetryLine(string Text, Guid Id);

    private static readonly string[] LogLevelNames = ["Trace", "Debug", "Info", "Warning", "Error"];

    private readonly IDiagnosticsService? _diagnostics;
    private readonly IFirstRunService? _firstRun;
    private readonly IMessageDumpService? _messageDump;

    private string _diagnosticsSummaryText = "尚未加载（点击「刷新」）";
    private IReadOnlyList<string> _recentMessages = [];
    private IReadOnlyList<RetryLine> _retryQueueLines = [];
    private RetryLine? _selectedRetryLine;
    private string _transferFeedback = "";
    private string _dumpFeedback = "";

    public MaintenanceSettingsPage(ISettingsService settingsService, IDiagnosticsService? diagnostics = null,
        IFirstRunService? firstRun = null, IMessageDumpService? messageDump = null)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        _diagnostics = diagnostics;
        _firstRun = firstRun;
        _messageDump = messageDump;
        InitializeComponent();
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
            DiagnosticsSummaryText = "排错服务未注册。";
            return;
        }

        try
        {
            var snapshot = await _diagnostics.GetSnapshotAsync().ConfigureAwait(true);
            var offline = snapshot.OfflineDuration is { } d ? $"{d.TotalMinutes:F0} 分钟" : "在线";
            DiagnosticsSummaryText =
                $"连接状态：{snapshot.ConnectionStatus}；协议端离线时长：{offline}；" +
                $"磁盘剩余：{(snapshot.DiskFreeMb is { } mb ? $"{mb} MB" : "未知")}；" +
                $"重试队列：{snapshot.RetryQueue.Count} 条";

            RecentMessages = snapshot.RecentMessages
                .Select(m => $"[{m.ReceivedAt:MM-dd HH:mm:ss}] group={m.GroupOpenId} {m.SenderNickname}：{m.Preview}")
                .ToList();

            RetryQueueLines = snapshot.RetryQueue
                .Select(i => new RetryLine(
                    $"[{i.Status}] {i.OperationType}｜尝试 {i.AttemptCount} 次｜下次 {i.NextAttemptAt:HH:mm:ss}｜{i.LastError}",
                    i.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticsSummaryText = $"获取排错快照失败：{ex.Message}";
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
            TransferFeedback = "已触发手动重连。";
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
            TransferFeedback = "请先在重试队列列表中选中一条。";
            return;
        }

        if (_diagnostics is null)
        {
            TransferFeedback = "重试队列未注册，无法重放。";
            return;
        }

        try
        {
            await _diagnostics.ReplayAsync(line.Id).ConfigureAwait(true);
            TransferFeedback = "已重放选中条目。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"重放失败：{ex.Message}";
        }
    }

    private async void OnExportDumpClicked(object? sender, RoutedEventArgs e)
    {
        if (_messageDump is null)
        {
            DumpFeedback = "消息日志 dump 服务未注册。";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            DumpFeedback = "无法获取窗口句柄，导出取消。";
            return;
        }

        string? filePath = null;
        try
        {
            // 用户可选保存路径；选择器不可用时兜底导出到桌面
            var picker = topLevel.StorageProvider;
            var file = await picker.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出消息日志 dump（JSONL）",
                SuggestedFileName = $"CyberTechRep-消息dump-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultExtension = "jsonl",
                FileTypeChoices = [new FilePickerFileType("JSONL（每行一条 JSON）")
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
                $"CyberTechRep-消息dump-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

            var count = await _messageDump.ExportAsync(filePath).ConfigureAwait(true);
            DumpFeedback = count == 0
                ? $"缓冲为空（尚未收到消息），已导出空文件：{filePath}"
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
            TransferFeedback = "首次启动引导服务未注册。";
            return;
        }

        try
        {
            var shown = await _firstRun.ShowWizardAsync().ConfigureAwait(true);
            TransferFeedback = shown ? "已打开首次启动引导窗口。" : "打开引导窗口失败，详见插件日志。";
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
            TransferFeedback = "已导出脱敏设置 JSON 到剪贴板。";
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
                TransferFeedback = "剪贴板没有可导入的设置 JSON。";
                return;
            }

            await SettingsService.ImportAsync(json).ConfigureAwait(true);
            TransferFeedback = "导入成功（敏感字段保留本机值）。";
        }
        catch (FormatException ex)
        {
            TransferFeedback = $"导入被拒绝：{ex.Message}";
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
