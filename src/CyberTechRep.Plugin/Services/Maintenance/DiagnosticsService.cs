using System.Runtime.Versioning;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Maintenance;

/// <summary>最近消息快照（排错面板展示用，脱敏）。</summary>
public sealed class MessageTraceItem
{
    public required string MessageId { get; init; }

    public required string GroupOpenId { get; init; }

    public string SenderNickname { get; init; } = "";

    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>摘要（text 段前 80 字符；不含完整原文，防泄漏）。</summary>
    public string Preview { get; init; } = "";
}

/// <summary>排错数据聚合快照。</summary>
public sealed class DiagnosticsSnapshot
{
    public ConnectionStatus ConnectionStatus { get; init; }

    public TimeSpan? OfflineDuration { get; init; }

    /// <summary>协议端离线时长是否超过告警阈值。</summary>
    public bool IsDiskSpaceLow { get; init; }

    /// <summary>磁盘剩余空间（MB；读取失败为 null）。</summary>
    public long? DiskFreeMb { get; init; }

    public IReadOnlyList<MessageTraceItem> RecentMessages { get; init; } = [];

    public IReadOnlyList<RetryQueueItem> RetryQueue { get; init; } = [];

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 排错数据出口：聚合连接状态、最近 N 条消息快照、重试队列内容与手动操作入口
/// （重放 / 手动重连），供设置页「维护」组展示。
/// 结构化日志本身由 ILogger 提供，这里只做聚合视图。
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>聚合当前排错快照（设置页打开时调用）。</summary>
    Task<DiagnosticsSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>手动重放指定重试条目（设置页「重放」按钮）。</summary>
    Task ReplayAsync(Guid id, CancellationToken ct = default);

    /// <summary>手动重连协议端（设置页「手动重连」按钮）。</summary>
    Task ReconnectAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IDiagnosticsService"/> 默认实现：订阅 <see cref="IMessageIngestService.MessageReceived"/>
/// 维护最近 N 条消息环形快照（默认 50 条）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiagnosticsService : IDiagnosticsService, IDisposable
{
    private const int MaxTraceItems = 50;

    private readonly object _lock = new();
    private readonly IMessageIngestService? _ingestService;
    private readonly IEnvironmentMonitorService? _environmentMonitor;
    private readonly IRetryQueueService? _retryQueue;
    private readonly ILogger _logger;
    private readonly Queue<MessageTraceItem> _recentMessages = new();

    public DiagnosticsService(IMessageIngestService? ingestService = null,
        IEnvironmentMonitorService? environmentMonitor = null,
        IRetryQueueService? retryQueue = null,
        ILogger? logger = null)
    {
        _ingestService = ingestService;
        _environmentMonitor = environmentMonitor;
        _retryQueue = retryQueue;
        _logger = logger ?? NullLogger.Instance;

        if (ingestService is not null)
        {
            ingestService.MessageReceived += OnMessageReceived;
        }
    }

    /// <summary>最近消息快照（最新在后）。</summary>
    public IReadOnlyList<MessageTraceItem> RecentMessages
    {
        get
        {
            lock (_lock)
            {
                return [.. _recentMessages];
            }
        }
    }

    /// <inheritdoc />
    public async Task<DiagnosticsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var diskFreeMb = _environmentMonitor switch
        {
            EnvironmentMonitorService concrete => concrete.FreeSpaceBytes / 1024L / 1024L,
            _ => null
        };

        return new DiagnosticsSnapshot
        {
            ConnectionStatus = _ingestService?.Status ?? ConnectionStatus.Disconnected,
            OfflineDuration = _environmentMonitor?.OfflineDuration,
            IsDiskSpaceLow = _environmentMonitor?.IsDiskSpaceLow() ?? false,
            DiskFreeMb = diskFreeMb,
            RecentMessages = RecentMessages,
            RetryQueue = _retryQueue is null ? [] : await _retryQueue.GetAllAsync(ct).ConfigureAwait(false)
        };
    }

    /// <inheritdoc />
    public async Task ReplayAsync(Guid id, CancellationToken ct = default)
    {
        if (_retryQueue is null)
        {
            _logger.LogWarning("重试队列未注册，无法重放：{Id}", id);
            return;
        }

        await _retryQueue.ReplayAsync(id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (_ingestService is null)
        {
            _logger.LogWarning("消息接入服务未注册，无法手动重连");
            return;
        }

        _logger.LogInformation("排错面板触发手动重连");
        await _ingestService.ReconnectAsync(ct).ConfigureAwait(false);
    }

    private void OnMessageReceived(object? sender, MessageRecord message)
    {
        var preview = string.Concat(
            message.Segments
                .Where(s => string.Equals(s.Type, SegmentTypes.Text, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Text ?? ""));
        if (preview.Length > 80)
        {
            preview = preview[..80];
        }

        var item = new MessageTraceItem
        {
            MessageId = message.MessageId,
            GroupOpenId = message.GroupOpenId,
            SenderNickname = message.SenderNickname,
            ReceivedAt = message.ReceivedAt,
            Preview = preview
        };

        lock (_lock)
        {
            _recentMessages.Enqueue(item);
            while (_recentMessages.Count > MaxTraceItems)
            {
                _recentMessages.Dequeue();
            }
        }
    }

    public void Dispose()
    {
        if (_ingestService is not null)
        {
            _ingestService.MessageReceived -= OnMessageReceived;
        }
    }
}
