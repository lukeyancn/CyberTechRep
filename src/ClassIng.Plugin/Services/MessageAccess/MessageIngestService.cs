using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.MessageAccess;

/// <summary>接入服务依赖提供者（解耦设置服务，便于单元测试与首次引导前使用默认值）。</summary>
public sealed class IngestOptionsProvider
{
    /// <summary>提供当前连接设置（设置页修改后热生效）。</summary>
    public required Func<ConnectionSettings> GetSettings { get; init; }

    /// <summary>解密 AppSecretProtected → 明文（默认直接返回原值，供测试使用；宿主注入 DPAPI 实现）。</summary>
    public Func<string, string> SecretUnprotector { get; init; } = v => v;

    /// <summary>幂等存储数据目录。</summary>
    public required string DataDirectory { get; init; }
}

/// <summary>
/// QQ 消息接入服务（模块 1 对外入口）。
/// 组合：官方网关 WebSocket 客户端 + 接入管道（映射/白名单/幂等）+ 结构化日志与连接状态广播。
/// </summary>
public sealed class MessageIngestService : IMessageIngestService
{
    private readonly IngestOptionsProvider _provider;
    private readonly ILogger? _logger;
    private readonly Action<QQOfficialWsClientOptions>? _configureClientOptions;
    private readonly object _lock = new();
    private QQOfficialWsClient? _client;
    private MessageIngestPipeline? _pipeline;
    private MessageIdempotencyStore? _store;
    private ConnectionSettings? _lastApplied;
    private volatile bool _started;

    public MessageIngestService(IngestOptionsProvider provider, ILogger? logger = null,
        Action<QQOfficialWsClientOptions>? configureClientOptions = null)
    {
        _provider = provider;
        _logger = logger;
        _configureClientOptions = configureClientOptions;
    }

    public ConnectionStatus Status => _client?.Status ?? ConnectionStatus.Disconnected;

    public event EventHandler<MessageRecord>? MessageReceived;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            var settings = _provider.GetSettings();
            _lastApplied = settings;

            Directory.CreateDirectory(_provider.DataDirectory);
            _store = new MessageIdempotencyStore(_provider.DataDirectory, CreateStoreLogger());
            _pipeline = new MessageIngestPipeline(_store, CreatePipelineLogger());
            _pipeline.MessageReceived += (_, m) => MessageReceived?.Invoke(this, m);
            _pipeline.UpdateSettings(settings);

            _client = CreateClient(settings);
            _client.ConnectionStateChanged += (_, s) =>
            {
                StatusChanged?.Invoke(this, s);
                if (s == ConnectionStatus.Reconnecting)
                {
                    _logger?.LogWarning("协议端连接中断，正在按指数退避策略重连");
                }
            };
            _client.DispatchReceived += OnDispatch;
            _ = _client.StartAsync(ct);
            _started = true;

            _logger?.LogInformation(
                "消息接入服务已启动：AppId={AppId} ApiBase={ApiBase} 白名单={Whitelist} 幂等记录={Seen}条",
                settings.AppId, settings.ApiBase, settings.GroupWhitelist.Count, _store.Count);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        QQOfficialWsClient? client;
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            client = _client;
            _started = false;
        }

        if (client is not null)
        {
            await client.StopAsync().ConfigureAwait(false);
        }

        _logger?.LogInformation("消息接入服务已停止");
    }

    public Task<IReadOnlyList<MessageRecord>> FetchHistoryAsync(int days, CancellationToken ct = default)
    {
        // 能力缺口（调研文档已声明）：官方平台不提供历史消息补拉 API。
        _logger?.LogWarning("官方机器人平台无历史消息补拉 API，FetchHistoryAsync({Days}) 返回空（历史回放能力暂缺）", days);
        return Task.FromResult<IReadOnlyList<MessageRecord>>([]);
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("排错面板触发手动重连");
        QQOfficialWsClient? client;
        lock (_lock)
        {
            client = _client;
        }

        if (client is not null)
        {
            await client.ReconnectAsync().ConfigureAwait(false);
        }
    }

    /// <summary>设置热更新（设置页/首次引导修改连接配置后调用）。</summary>
    public void ApplySettings(ConnectionSettings settings)
    {
        QQOfficialWsClient? client;
        lock (_lock)
        {
            if (!_started || _pipeline is null)
            {
                _lastApplied = settings;
                return;
            }

            _pipeline.UpdateSettings(settings);
            client = _client;
            _lastApplied = settings;
        }

        // 连接参数（AppId/Secret/ApiBase）变更需要重建连接：直接重连
        if (RequiresReconnect(_lastApplied, settings) && client is not null)
        {
            _logger?.LogInformation("连接参数变更，重连生效");
            _ = client.ReconnectAsync();
        }
    }

    private void OnDispatch(object? sender, (string Type, string Data) e)
    {
        try
        {
            _pipeline?.HandleDispatch(e.Type, e.Data);
        }
        catch (Exception ex)
        {
            // 任何单条事件处理失败不得影响主循环
            _logger?.LogError(ex, "分发事件处理失败：type={Type}", e.Type);
        }
    }

    private QQOfficialWsClient CreateClient(ConnectionSettings settings)
    {
        var options = new QQOfficialWsClientOptions
        {
            AppId = settings.AppId,
            AppSecretPlain = _provider.SecretUnprotector(settings.AppSecretProtected),
            ApiBase = settings.ApiBase,
            TokenApiUrl = settings.TokenApiUrl,
            ReconnectInitialDelayMs = settings.ReconnectInitialDelaySec * 1000,
            ReconnectBackoffFactor = settings.ReconnectBackoffFactor,
            ReconnectMaxDelayMs = settings.ReconnectMaxDelaySec * 1000
        };
        _configureClientOptions?.Invoke(options);
        return new QQOfficialWsClient(options, CreateClientLogger());
    }

    private static bool RequiresReconnect(ConnectionSettings? oldSettings, ConnectionSettings newSettings)
    {
        return oldSettings is null
            || oldSettings.AppId != newSettings.AppId
            || oldSettings.ApiBase != newSettings.ApiBase
            || oldSettings.TokenApiUrl != newSettings.TokenApiUrl;
    }

    // 分类日志器：结构化日志中统一带 SourceContext，排错面板可按类别过滤
    private ILogger CreateClientLogger() => ((ILogger?)_logger) ?? NullLogger.Instance;
    private ILogger CreatePipelineLogger() => ((ILogger?)_logger) ?? NullLogger.Instance;
    private ILogger CreateStoreLogger() => ((ILogger?)_logger) ?? NullLogger.Instance;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        QQOfficialWsClient? client;
        lock (_lock)
        {
            client = _client;
            _client = null;
        }

        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
