using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Maintenance;

/// <summary>
/// 重试队列依赖提供者（解耦设置服务，便于单元测试）。
/// </summary>
public sealed class RetryQueueOptions
{
    /// <summary>提供维护设置（重试开关 / 上限次数）。</summary>
    public required Func<MaintenanceSettings> GetSettings { get; init; }

    /// <summary>持久化目录（retry-queue.json 存放处）。</summary>
    public required string DataDirectory { get; init; }

    /// <summary>持久化文件名（相对 DataDirectory）。</summary>
    public string QueueFileName { get; init; } = "retry-queue.json";

    /// <summary>指数退避初始延迟（秒）。</summary>
    public int InitialDelaySec { get; init; } = 5;

    /// <summary>指数退避倍增系数。</summary>
    public double BackoffFactor { get; init; } = 2.0;

    /// <summary>退避上限（秒）。</summary>
    public int MaxDelaySec { get; init; } = 600;

    /// <summary>是否启动后台调度循环（单元测试置 false，手动调 TickAsync）。</summary>
    public bool AutoStartScheduler { get; init; } = true;

    /// <summary>后台调度扫描间隔（秒）。</summary>
    public int ScanIntervalSec { get; init; } = 10;
}

/// <summary>
/// 单次重试执行器：返回 true 表示成功；false/抛异常表示失败（继续退避重试）。
/// 入参为入队时的 payload JSON（已脱敏快照）。
/// </summary>
public delegate Task<bool> RetryExecutorAsync(string payloadJson, CancellationToken ct);

/// <summary>
/// 模块 8：失败重试队列。
/// <para>
/// - JSON 持久化（retry-queue.json，原子写入），跨插件重启保留。
/// - 指数退避调度：NextAttemptAt = Now + min(InitialDelay × Factor^AttemptCount, MaxDelay)。
/// - 超过上限（MaintenanceSettings.MaxRetryAttempts）置 <see cref="RetryItemStatus.GivenUp"/>，
///   等待设置页手动重放（<see cref="ReplayAsync"/> 重置计数）；
/// - 操作执行器按 <see cref="RetryOperationType"/> 注册（<see cref="RegisterExecutor"/>）：
///   本模块内有 StoreWrite / Custom 注册点；FileDownload / SubjectClassify 依赖模块 4 文件管道与
///   模块 3 AI 识别链的重试入口，尚未完成，仅保留注册点（运行时未注册执行器的条目按失败退避，不丢失）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RetryQueueService : IRetryQueueService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly object _lock = new();
    private readonly RetryQueueOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<RetryOperationType, RetryExecutorAsync> _executors = new();
    private List<RetryQueueItem> _items = [];
    private Timer? _scheduler;
    private volatile bool _disposed;

    public RetryQueueService(RetryQueueOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _items = LoadOrSeed();
        RegisterDefaults();

        if (options.AutoStartScheduler)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.ScanIntervalSec));
            _scheduler = new Timer(
                static state => ((RetryQueueService)state!).OnSchedulerTick(),
                this,
                interval,
                interval);
        }
    }

// ============ 公共 API（IRetryQueueService 契约）============

/// <summary>入队一个失败操作（初始 NextAttemptAt = Now + 初始退避）。</summary>
    public Task EnqueueAsync(RetryOperationType type, object payload, CancellationToken ct = default)
    {
        var payloadJson = SerializePayload(payload);
        var settings = SafeGetSettings();

        var item = new RetryQueueItem
        {
            OperationType = type,
            PayloadJson = payloadJson,
            Status = RetryItemStatus.Waiting,
            AttemptCount = 0,
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(_options.InitialDelaySec),
            CreatedAt = DateTimeOffset.UtcNow
        };

        lock (_lock)
        {
            _items.Add(item);
        }

        _logger.LogInformation("重试队列入队：{Type}（Id={Id}），上限 {MaxAttempts} 次",
            type, item.Id, settings.MaxRetryAttempts);
        Persist();
        return Task.CompletedTask;
    }

/// <summary>全部条目快照（设置页「维护」组展示）。</summary>
    public Task<IReadOnlyList<RetryQueueItem>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<RetryQueueItem>>([.. _items]);
        }
    }

    /// <summary>
    /// 手动重放（排错面板按钮）：重置尝试计数与状态为 Waiting，NextAttemptAt=Now，
    /// 立即尝试一次执行。
    /// </summary>
    public async Task ReplayAsync(Guid id, CancellationToken ct = default)
    {
        RetryQueueItem? item;
        lock (_lock)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                _logger.LogWarning("ReplayAsync：未找到重试条目 {Id}", id);
                return;
            }

            item.AttemptCount = 0;
            item.Status = RetryItemStatus.Waiting;
            item.NextAttemptAt = DateTimeOffset.UtcNow;
            item.LastError = null;
        }

        _logger.LogInformation("手动重放重试条目 {Id}（{Type}）", id, item.OperationType);
        Persist();
        await TickAsync(ct).ConfigureAwait(false);
    }

/// <summary>清空已成功条目。</summary>
    public Task PurgeSucceededAsync(CancellationToken ct = default)
    {
        int removed;
        lock (_lock)
        {
            removed = _items.RemoveAll(i => i.Status == RetryItemStatus.Succeeded);
        }

        if (removed > 0)
        {
            _logger.LogInformation("清理已成功重试条目 {Count} 条", removed);
            Persist();
        }

        return Task.CompletedTask;
    }

/// <summary>注册指定操作类型的执行器（模块内 / 后续模块接入点）。</summary>
    public void RegisterExecutor(RetryOperationType type, RetryExecutorAsync executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        lock (_lock)
        {
            _executors[type] = executor;
        }
    }

    // ============ 调度核心 ============

    /// <summary>
    /// 扫描到期条目并执行（内部方法，internal 供单元测试驱动；后台 Timer 亦调用此方法）。
    /// </summary>
    internal Task TickAsync(CancellationToken ct = default) => TickAsync(forceDue: false, ct);

    /// <summary>
    /// 扫描并执行条目（forceDue=true 时忽略 NextAttemptAt，测试驱动用）。
    /// </summary>
    internal async Task TickAsync(bool forceDue, CancellationToken ct = default)
    {
        if (SafeGetSettings().RetryQueueEnabled is false)
        {
            return;
        }

        RetryQueueItem[] due;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            due = _items
                .Where(i => i.Status == RetryItemStatus.Waiting &&
                            (forceDue || i.NextAttemptAt <= now))
                .ToArray();
        }

        foreach (var item in due)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteItemAsync(item, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 指数退避计算（纯函数，供单元测试）：min(InitialDelaySec × BackoffFactor^AttemptCount, MaxDelaySec)。
    /// </summary>
    internal int ComputeNextDelaySec(int attemptCount)
    {
        var baseDelay = Math.Max(0, _options.InitialDelaySec);
        var factor = Math.Max(1.0, _options.BackoffFactor);
        var maxDelay = Math.Max(baseDelay, Math.Max(0, _options.MaxDelaySec));

        var capped = Math.Min(attemptCount, 16); // 防溢出：16 次倍增已远超上限
        var delay = baseDelay * Math.Pow(factor, capped);
        return (int)Math.Min(maxDelay, Math.Ceiling(delay));
    }

    private async Task ExecuteItemAsync(RetryQueueItem item, CancellationToken ct)
    {
        RetryExecutorAsync? executor;
        lock (_lock)
        {
            item.Status = RetryItemStatus.Retrying;
            executor = _executors.GetValueOrDefault(item.OperationType);
        }

        bool success;
        string? error = null;
        try
        {
            if (executor is null)
            {
                // 执行器未注册（如 FileDownload/SubjectClassify 依赖未完成模块）：按失败退避，条目不丢失
                error = $"executor_not_registered:{item.OperationType}";
                success = false;
            }
            else
            {
                success = await executor(item.PayloadJson, ct).ConfigureAwait(false);
                error = success ? null : "executor_returned_false";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                item.Status = RetryItemStatus.Waiting;
            }

            throw;
        }
        catch (Exception ex)
        {
            success = false;
            error = $"{ex.GetType().Name}:{ex.Message}";
        }

        var settings = SafeGetSettings();
        lock (_lock)
        {
            item.AttemptCount++;
            if (success)
            {
                item.Status = RetryItemStatus.Succeeded;
                item.NextAttemptAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("重试成功：{Type}（Id={Id}），第 {Attempt} 次",
                    item.OperationType, item.Id, item.AttemptCount);
            }
            else if (item.AttemptCount >= Math.Max(1, settings.MaxRetryAttempts))
            {
                item.Status = RetryItemStatus.GivenUp;
                item.LastError = error;
                _logger.LogWarning(
                    "重试放弃（达到上限 {MaxAttempts}）：{Type}（Id={Id}），LastError={Error}；可在设置页手动重放",
                    settings.MaxRetryAttempts, item.OperationType, item.Id, error);
            }
            else
            {
                item.Status = RetryItemStatus.Waiting;
                item.LastError = error;
                item.NextAttemptAt =
                    DateTimeOffset.UtcNow.AddSeconds(ComputeNextDelaySec(item.AttemptCount));
                _logger.LogInformation(
                    "重试失败（第 {Attempt} 次）：{Type}（Id={Id}），下次 {NextAt:HH:mm:ss}，Error={Error}",
                    item.AttemptCount, item.OperationType, item.Id, item.NextAttemptAt, error);
            }
        }

        Persist();
    }

    // ============ 持久化 ============

    private string QueueFilePath => Path.Combine(_options.DataDirectory, _options.QueueFileName);

    private List<RetryQueueItem> LoadOrSeed()
    {
        try
        {
            if (!File.Exists(QueueFilePath))
            {
                return [];
            }

            using var stream = File.OpenRead(QueueFilePath);
            var loaded = JsonSerializer.Deserialize<List<RetryQueueItem>>(stream, JsonOptions);
            return loaded ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取重试队列失败，从空队列开始：{Path}", QueueFilePath);
            return [];
        }
    }

    private void Persist()
    {
        List<RetryQueueItem> snapshot;
        lock (_lock)
        {
            snapshot = [.. _items];
        }

        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            var tempPath = QueueFilePath + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush();
            }

            File.Move(tempPath, QueueFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试队列持久化失败：{Path}", QueueFilePath);
        }
    }

    // ============ 内置执行器注册点 ============

    /// <summary>
    /// 内置注册：StoreWrite / Custom。StoreWrite 目前无具体存储回调（模块 5 未完成）。
    /// 注册的是"透传执行器"，由 <see cref="StoreWriteCallback"/> 注入真实回调；未注入时按失败退避。
    /// </summary>
    private void RegisterDefaults()
    {
        RegisterExecutor(RetryOperationType.StoreWrite, OnStoreWriteAsync);
        RegisterExecutor(RetryOperationType.Custom, OnCustomAsync);
    }

    /// <summary>StoreWrite 真实回调（模块 5 完成后注入）。</summary>
    public Func<string, CancellationToken, Task<bool>>? StoreWriteCallback { get; set; }

    /// <summary>Custom 真实回调（排错面板自定义重放）。</summary>
    public Func<string, CancellationToken, Task<bool>>? CustomCallback { get; set; }

    private async Task<bool> OnStoreWriteAsync(string payloadJson, CancellationToken ct)
    {
        var callback = StoreWriteCallback;
        if (callback is null)
        {
            _logger.LogWarning("StoreWrite 执行器回调未注入（模块 5 未完成），按失败退出");
            return false;
        }

        return await callback(payloadJson, ct).ConfigureAwait(false);
    }

    private async Task<bool> OnCustomAsync(string payloadJson, CancellationToken ct)
    {
        var callback = CustomCallback;
        if (callback is null)
        {
            _logger.LogWarning("Custom 执行器回调未注入，按失败退出");
            return false;
        }

        return await callback(payloadJson, ct).ConfigureAwait(false);
    }

    // ============ 辅助 ============

    private MaintenanceSettings SafeGetSettings()
    {
        try
        {
            return _options.GetSettings() ?? new MaintenanceSettings();
        }
        catch
        {
            return new MaintenanceSettings();
        }
    }

    private static string SerializePayload(object payload)
    {
        if (payload is string s)
        {
            return s;
        }

        try
        {
            return JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch
        {
            // payload 不可序列化：存类型名占位（脱敏原则：绝不落密文）
            return $"{{\"unserializable\":\"{payload.GetType().FullName}\"}}";
        }
    }

    private void OnSchedulerTick()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            TickAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试队列后台调度异常");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler?.Dispose();
        _scheduler = null;
    }
}
