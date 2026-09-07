using System.Text.Json;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.MessageAccess;

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

    // NapCat 模式网关（官方模式下与 _client 同一实例，StopAsync/DisposeAsync 经 _gateway 统一收口）
    private IMessageGatewayClient? _gateway;

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

    public ConnectionStatus Status => _gateway?.Status ?? ConnectionStatus.Disconnected;

    /// <summary>
    /// NapCat 反向 WS 监听当前实际绑定的端口（未启用反向监听或非 NapCat 模式时为 null）。
    /// 供 NapCatRunnerService 判断「端口被占用」是否为本插件自身监听所致。
    /// </summary>
    public int? ActiveReverseListenerPort => (_gateway as NapCatWsClient)?.ListeningPort;

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
            _lastApplied = CloneSettings(settings);

            Directory.CreateDirectory(_provider.DataDirectory);
            _store = new MessageIdempotencyStore(_provider.DataDirectory, CreateStoreLogger());
            _pipeline = new MessageIngestPipeline(_store, CreatePipelineLogger());
            _pipeline.MessageReceived += (_, m) => MessageReceived?.Invoke(this, m);
            _pipeline.UpdateSettings(settings);

            // 模式分发：官方模式走原路径（_client 保持原语义，ApplySettings 热重连逻辑不变）；
            // NapCat 模式构建 NapCat 网关（_client 置空，事件与状态经 _gateway 统一接线）
            if (settings.Mode == MessageConnectionMode.NapCat)
            {
                _client = null;
                _gateway = CreateNapCatGateway(settings);
                AttachGatewayHandlers(_gateway);
                _ = _gateway.StartAsync(ct);
            }
            else
            {
                _client = CreateClient(settings);
                _gateway = _client;
                AttachGatewayHandlers(_client);
                _ = _client.StartAsync(ct);
            }
            _started = true;

            if (settings.Mode == MessageConnectionMode.NapCat)
            {
                _logger?.LogInformation(
                    "消息接入服务已启动：模式=NapCat 正向WS={WsUrl} 反向端口={Port} 白名单={Whitelist} 幂等记录={Seen}条",
                    settings.NapCatWsUrl, settings.NapCatReversePort, settings.GroupWhitelist.Count, _store.Count);
            }
            else
            {
                _logger?.LogInformation(
                    "消息接入服务已启动：AppId={AppId} ApiBase={ApiBase} 白名单={Whitelist} 幂等记录={Seen}条",
                    MaskAppId(settings.AppId), settings.ApiBase, settings.GroupWhitelist.Count, _store.Count);
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IMessageGatewayClient? gateway;
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            // 官方模式下 _gateway 即 _client，行为不变；NapCat 模式经统一抽象停止
            gateway = _gateway;
            _started = false;
        }

        if (gateway is not null)
        {
            await gateway.StopAsync().ConfigureAwait(false);
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
        QQOfficialWsClient? officialClient;
        IMessageGatewayClient? gateway;
        lock (_lock)
        {
            officialClient = _client;
            gateway = _gateway;
        }

        if (officialClient is not null)
        {
            // 官方模式：保持原语义（会话 Resume 优先）
            await officialClient.ReconnectAsync().ConfigureAwait(false);
        }
        else if (gateway is not null)
        {
            // NapCat 模式：按当前设置重建网关（顺带拾取正向地址/反向端口/token 的修改）
            await RestartNapCatGatewayAsync().ConfigureAwait(false);
        }
    }

    /// <summary>NapCat 模式网关重建：旧网关停止销毁，新网关以当前设置启动（排错面板手动重连触发）。</summary>
    private async Task RestartNapCatGatewayAsync()
    {
        IMessageGatewayClient? oldGateway;
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            oldGateway = _gateway;
            var settings = _provider.GetSettings();
            _lastApplied = CloneSettings(settings);
            _gateway = CreateNapCatGateway(settings);
            AttachGatewayHandlers(_gateway);
        }

        await _gateway!.StartAsync().ConfigureAwait(false);

        if (oldGateway is not null && !ReferenceEquals(oldGateway, _gateway))
        {
            try
            {
                await oldGateway.StopAsync().ConfigureAwait(false);
                await oldGateway.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "旧 NapCat 网关清理失败（不影响新网关运行）");
            }
        }
    }

    /// <summary>设置热更新（设置页/首次引导修改连接配置后调用；设置广播路径自动触发）。</summary>
    public void ApplySettings(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        QQOfficialWsClient? client;
        bool reconnectRequired;
        lock (_lock)
        {
            // 差异判定必须先于快照更新，且快照是独立副本：ConnectionSettings 是设置服务的活实例
            //（GetSettings 每次返回同一 Current.Connection，且可能被设置页绑定原地修改）。
            // 原缺陷：先把 _lastApplied 指向新值再与它比较，差异判定恒为 false（死代码），
            // 连接参数变更后从不重连；且 _lastApplied 与新值共享同一实例时引用/字段比较同样恒等。
            reconnectRequired = RequiresReconnect(_lastApplied, settings);
            _lastApplied = CloneSettings(settings);

            if (!_started || _pipeline is null)
            {
                return;
            }

            // 管道参数（群白名单）即时生效，无需重连
            _pipeline.UpdateSettings(settings);
            client = _client;
        }

        // 连接参数（AppId/ApiBase/TokenApiUrl）变更需要重建连接：直接重连。
        // 无变化（如悬浮窗拖拽等其他组的保存广播）不重连；AppSecret 密文因 DPAPI 非确定性
        // 加密每次保存都会变，不参与差异判定（密文变更经排错面板手动重连或重启生效，与现状一致）。
        if (reconnectRequired && client is not null)
        {
            _logger?.LogInformation("连接参数变更（AppId/ApiBase/TokenApiUrl），重连生效");
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

    /// <summary>NapCat（OneBot 11）模式网关构建（重连参数与官方模式共用同一组设置）。</summary>
    private NapCatWsClient CreateNapCatGateway(ConnectionSettings settings)
    {
        var options = new NapCatWsClientOptions
        {
            WsUrl = settings.NapCatWsUrl,
            // 设置语义：0 = 不启用反向监听 → 客户端传 -1（禁用）；>0 原样传入（客户端 0 = 系统分配，测试用）
            ReverseListenPort = settings.NapCatReversePort > 0 ? settings.NapCatReversePort : -1,
            AccessTokenPlain = _provider.SecretUnprotector(settings.NapCatAccessTokenProtected),
            ReconnectInitialDelayMs = settings.ReconnectInitialDelaySec * 1000,
            ReconnectBackoffFactor = settings.ReconnectBackoffFactor,
            ReconnectMaxDelayMs = settings.ReconnectMaxDelaySec * 1000
        };
        return new NapCatWsClient(options, CreateClientLogger());
    }

    /// <summary>网关事件统一接线（分发透传 + 状态广播），官方与 NapCat 模式共用。</summary>
    private void AttachGatewayHandlers(IMessageGatewayClient gateway)
    {
        gateway.ConnectionStateChanged += (_, s) =>
        {
            StatusChanged?.Invoke(this, s);
            if (s == ConnectionStatus.Reconnecting)
            {
                _logger?.LogWarning("协议端连接中断，正在按指数退避策略重连");
            }
        };
        gateway.DispatchReceived += OnDispatch;
    }

    // internal：供单测直接验证连接参数差异判定
    internal static bool RequiresReconnect(ConnectionSettings? oldSettings, ConnectionSettings newSettings)
    {
        return oldSettings is null
            || oldSettings.AppId != newSettings.AppId
            || oldSettings.ApiBase != newSettings.ApiBase
            || oldSettings.TokenApiUrl != newSettings.TokenApiUrl;
    }

    /// <summary>连接设置独立副本（JSON 往返）：_lastApplied 必须与设置服务的活实例解耦，
    /// 否则活实例被原地修改后新旧差异判定永远相等（ApplySettings 的重连判定会失效）。</summary>
    private static ConnectionSettings CloneSettings(ConnectionSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        return JsonSerializer.Deserialize<ConnectionSettings>(json)!;
    }

    // 分类日志器：结构化日志中统一带 SourceContext，排错面板可按类别过滤
    private ILogger CreateClientLogger() => ((ILogger?)_logger) ?? NullLogger.Instance;
    private ILogger CreatePipelineLogger() => ((ILogger?)_logger) ?? NullLogger.Instance;
    private ILogger CreateStoreLogger() => ((ILogger?)_logger) ?? NullLogger.Instance;

    /// <summary>日志脱敏：AppId 只保留前 4 位，避免完整标识符进入日志。</summary>
    private static string MaskAppId(string appId)
        => string.IsNullOrEmpty(appId) ? "(空)"
            : appId.Length <= 4 ? appId : appId[..4] + "***";

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        QQOfficialWsClient? client;
        IMessageGatewayClient? gateway;
        lock (_lock)
        {
            client = _client;
            gateway = _gateway;
            _client = null;
            _gateway = null;
        }

        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (gateway is not null && !ReferenceEquals(gateway, client))
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
        }
    }
}
