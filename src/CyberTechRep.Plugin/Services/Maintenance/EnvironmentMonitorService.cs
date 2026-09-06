using System.Runtime.Versioning;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Maintenance;

/// <summary>环境监测选项（可测试注入）。</summary>
public sealed class EnvironmentMonitorOptions
{
    /// <summary>磁盘剩余空间监测目录（默认插件数据目录；取其所在盘符）。</summary>
    public required string MonitoredDirectory { get; init; }

    /// <summary>磁盘剩余空间告警阈值（MB）。</summary>
    public long DiskFreeThresholdMb { get; init; } = 512;

    /// <summary>协议端离线告警阈值（分钟）。</summary>
    public int OfflineWarningMinutes { get; init; } = 10;

    /// <summary>周期自检间隔（秒）；0=不启动周期自检（仅事件驱动）。</summary>
    public int CheckIntervalSeconds { get; init; } = 60;

    /// <summary>自由空间提供者（默认 DriveInfo；测试可注入）。</summary>
    public Func<long>? FreeSpaceBytesProvider { get; init; }

    /// <summary>总空间提供者（默认 DriveInfo；测试可注入）。</summary>
    public Func<long>? TotalSpaceBytesProvider { get; init; }
}

/// <summary>
/// 模块 8：环境状态探测。
/// <para>
/// - 磁盘剩余空间：低于阈值（默认 512 MB）时通过 <see cref="WarningRaised"/> 告警；
/// - 协议端离线时长：订阅 <see cref="IMessageIngestService.StatusChanged"/>，离线开始计时，
///   超过阈值（默认 10 分钟）告警；
/// - 告警去抖：同一问题在解除前只告警一次。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnvironmentMonitorService : IEnvironmentMonitorService, IDisposable
{
    private readonly object _lock = new();
    private readonly EnvironmentMonitorOptions _options;
    private readonly IMessageIngestService? _ingestService;
    private readonly ILogger _logger;
    private System.Threading.Timer? _timer;
    private DateTimeOffset? _offlineSince;
    private bool _diskWarningActive;
    private bool _offlineWarningActive;
    private volatile bool _disposed;

    /// <inheritdoc />
    public event EventHandler<string>? WarningRaised;

    public EnvironmentMonitorService(EnvironmentMonitorOptions options, IMessageIngestService? ingestService = null,
        ILogger? logger = null)
    {
        _options = options;
        _ingestService = ingestService;
        _logger = logger ?? NullLogger.Instance;

        if (ingestService is not null)
        {
            ingestService.StatusChanged += OnStatusChanged;
            // 初始状态同步
            if (ingestService.Status is not (ConnectionStatus.Connected or ConnectionStatus.Disconnected))
            {
                MarkOffline();
            }
        }

        if (options.CheckIntervalSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(options.CheckIntervalSeconds);
            _timer = new System.Threading.Timer(
                static state => ((EnvironmentMonitorService)state!).CheckNow(),
                this,
                interval,
                interval);
        }
    }

    /// <summary>磁盘剩余空间（字节）；获取失败返回 null。</summary>
    public long? FreeSpaceBytes
    {
        get
        {
            try
            {
                if (_options.FreeSpaceBytesProvider is { } provider)
                {
                    return provider();
                }

                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_options.MonitoredDirectory))!);
                return drive.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取磁盘剩余空间失败：{Directory}", _options.MonitoredDirectory);
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsDiskSpaceLow()
    {
        var free = FreeSpaceBytes;
        if (free is null)
        {
            return false; // 读取失败不误报
        }

        return IsDiskSpaceLowCore(free.Value, _options.DiskFreeThresholdMb * 1024L * 1024L);
    }

    /// <summary>磁盘阈值判断（纯函数，供单元测试）。</summary>
    internal static bool IsDiskSpaceLowCore(long freeBytes, long thresholdBytes)
        => freeBytes < thresholdBytes;

    /// <inheritdoc />
    public TimeSpan? OfflineDuration
    {
        get
        {
            lock (_lock)
            {
                return _offlineSince is { } since ? DateTimeOffset.UtcNow - since : null;
            }
        }
    }

    /// <summary>当前一次自检（内部驱动 / 测试手动调用）。</summary>
    public void CheckNow()
    {
        if (_disposed)
        {
            return;
        }

        // 磁盘告警（去抖：解除后允许再次告警）
        var diskLow = IsDiskSpaceLow();
        if (diskLow && !_diskWarningActive)
        {
            _diskWarningActive = true;
            RaiseWarning($"磁盘剩余空间低于阈值（{_options.DiskFreeThresholdMb} MB）");
        }
        else if (!diskLow && _diskWarningActive)
        {
            _diskWarningActive = false;
            _logger.LogInformation("磁盘剩余空间已恢复到阈值以上");
        }

        // 离线时长告警（去抖）
        var offline = OfflineDuration;
        if (offline is { } duration && duration >= TimeSpan.FromMinutes(_options.OfflineWarningMinutes))
        {
            if (!_offlineWarningActive)
            {
                _offlineWarningActive = true;
                RaiseWarning($"协议端已离线 {duration.TotalMinutes:F0} 分钟");
            }
        }
        else if (offline is null && _offlineWarningActive)
        {
            _offlineWarningActive = false;
        }
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected)
        {
            MarkOnline();
        }
        else
        {
            MarkOffline();
        }
    }

    internal void MarkOffline()
    {
        lock (_lock)
        {
            _offlineSince ??= DateTimeOffset.UtcNow;
        }
    }

    internal void MarkOnline()
    {
        lock (_lock)
        {
            _offlineSince = null;
        }

        _offlineWarningActive = false;
        _logger.LogInformation("协议端已恢复连接，离线计时清零");
    }

    private void RaiseWarning(string message)
    {
        _logger.LogWarning("环境告警：{Message}", message);
        try
        {
            WarningRaised?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WarningRaised 订阅方处理异常");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ingestService is not null)
        {
            _ingestService.StatusChanged -= OnStatusChanged;
        }

        _timer?.Dispose();
        _timer = null;
    }
}
