using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Pipeline;

/// <summary>
/// 保留期清理任务（模块 5 存储层配套）：
/// 在插件启动时、每日跨天（本地 0 点）与设置变更时调用
/// <see cref="INoticeStore.CleanupAsync"/> / <see cref="IHomeworkStore.CleanupAsync"/>。
/// 清理纪律（<see cref="Stores.RetentionPolicies"/>）：只删过期桶，绝不动当天与未读；
/// 保留天数为 0（默认）时不清理。任何异常只记日志，绝不阻断插件运行。
/// </summary>
public sealed class RetentionCleanupService : IHostedService, IDisposable
{
    private readonly INoticeStore? _noticeStore;
    private readonly IHomeworkStore? _homeworkStore;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger _logger;
    private Timer? _timer;
    private int _running;
    private bool _disposed;

    public RetentionCleanupService(
        INoticeStore? noticeStore = null,
        IHomeworkStore? homeworkStore = null,
        ISettingsService? settingsService = null,
        ILogger? logger = null)
    {
        _noticeStore = noticeStore;
        _homeworkStore = homeworkStore;
        _settingsService = settingsService;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动即清一次（旧数据按 CreatedAt 归桶，首次清理不丢未读）
        RunSafe("startup");
        ArmNextMidnightTimer();

        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        _logger.LogInformation(
            "保留期清理任务已启动：NoticeStore={Notice}, HomeworkStore={Homework}",
            _noticeStore is not null, _homeworkStore is not null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
    }

    private void OnSettingsChanged(object? sender, AppSettings e) => RunSafe("settings-changed");

    /// <summary>武装次日 0 点（本地）一次性定时器，触发后重新武装（跨天清理 + 重排）。</summary>
    private void ArmNextMidnightTimer()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var now = DateTime.Now;
            var due = now.Date.AddDays(1) - now;
            _timer?.Dispose();
            _timer = new Timer(
                _ =>
                {
                    RunSafe("day-rollover");
                    ArmNextMidnightTimer();
                },
                null,
                due,
                Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跨天清理定时器武装失败（清理将在下次启动/设置变更时执行）");
        }
    }

    private void RunSafe(string trigger)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return; // 上一次清理尚未结束：跳过本次（幂等，无并发风险）
        }

        try
        {
            var noticesRemoved = _noticeStore?.CleanupAsync(CancellationToken.None).GetAwaiter().GetResult() ?? 0;
            var homeworkRemoved = _homeworkStore?.CleanupAsync(CancellationToken.None).GetAwaiter().GetResult() ?? 0;
            if (noticesRemoved > 0 || homeworkRemoved > 0)
            {
                _logger.LogInformation(
                    "保留期清理执行完成（Trigger={Trigger}）：通知删除 {Notices} 条，作业删除 {Homework} 条",
                    trigger, noticesRemoved, homeworkRemoved);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保留期清理执行失败（Trigger={Trigger}），不影响插件其余功能", trigger);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
