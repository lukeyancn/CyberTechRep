using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>NapCat（OneBot 11）WebSocket 客户端运行参数。</summary>
public sealed class NapCatWsClientOptions
{
    /// <summary>正向 WS：NapCat「WebSocket 服务器」地址（非空时优先；如 ws://127.0.0.1:3001）。</summary>
    public string WsUrl { get; set; } = "";

    /// <summary>反向 WS：本插件作为 WS 服务端监听的端口（0 = 系统分配端口；负数 = 不启用反向监听）。</summary>
    public int ReverseListenPort { get; set; }

    /// <summary>access token 明文（由设置服务解密后传入；日志中永不出现）。</summary>
    public string AccessTokenPlain { get; set; } = "";

    public int ReconnectInitialDelayMs { get; set; } = 2000;

    public double ReconnectBackoffFactor { get; set; } = 2.0;

    public int ReconnectMaxDelayMs { get; set; } = 300_000;

    /// <summary>WebSocket 连接工厂（默认 ClientWebSocket；单元测试注入本地测试服务器）。</summary>
    public Func<Uri, CancellationToken, Task<WebSocket>>? SocketFactory { get; set; }
}

/// <summary>
/// NapCat（OneBot 11）WebSocket 网关客户端，官方 <see cref="QQOfficialWsClient"/> 的可选替代：
/// <list type="bullet">
/// <item>正向 WS：作为客户端连接 NapCat 的 WS 服务端（可选 Authorization: Bearer 头鉴权）；</item>
/// <item>反向 WS：本插件作为 WS 服务端监听指定端口，NapCat 主动接入；
/// token 已配置时校验（查询参数 access_token 或 Authorization 头），不匹配以 HTTP 401 拒绝；</item>
/// <item>事件处理：meta_event（心跳/生命周期）忽略；post_type=message 事件经
/// <see cref="NapCatEventNormalizer"/> 规范化为官方事件 JSON 后经 DispatchReceived 下发，
/// 接入管道（白名单/幂等）零改动复用；file 段消息分发前经 <see cref="NapCatFileUrlResolver"/>
/// 通过同一连接调用 get_group_file_url 补全/刷新下载直链；echo 回包路由给在途 API 调用。</item>
/// </list>
/// 断线重连沿用与官方客户端相同的指数退避；状态经同一 ConnectionStateChanged 机制广播。
/// 反向 WS 服务端用 TcpListener + WebSocket.CreateFromStream 自实现（零新增依赖）。
/// </summary>
public sealed class NapCatWsClient : IMessageGatewayClient
{
    private const string WsMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly NapCatWsClientOptions _options;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private volatile bool _disposed;

    /// <summary>NapCat API 调用超时（群文件直链解析/历史拉取用；超时按失败处理，不中断接收循环）。</summary>
    private static readonly TimeSpan ApiCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>等待 OneBot 生命周期/心跳的最长时间：超时按「传输层就绪但未确认」降级判定（记警告）。</summary>
    private static readonly TimeSpan LifecycleGraceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>心跳检查间隔（连接僵死判定用）。</summary>
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>心跳/生命周期事件静默多久后判定连接僵死并主动重连（已至少收到过一次心跳后生效）。</summary>
    private static readonly TimeSpan HeartbeatSilenceTimeout = TimeSpan.FromSeconds(90);

    /// <summary>在途 API 调用（echo → 完成源；接收循环按 echo 路由回包）。</summary>
    private readonly Dictionary<string, TaskCompletionSource<JsonElement?>> _pendingApiCalls =
        new(StringComparer.Ordinal);

    /// <summary>WebSocket 发送锁：接收循环与直链解析任务并发发送，单写者保证。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>当前活跃连接（供 <see cref="CallApiAsync"/> 从接收循环外发起 API 调用）。</summary>
    private volatile WebSocket? _activeSocket;

    /// <summary>最近一次收到 meta_event（心跳/生命周期）的 UTC 时间。</summary>
    private DateTime _lastMetaEventUtc = DateTime.MinValue;

    /// <summary>本次连接是否已收到过生命周期/心跳（心跳僵死判定只对「曾收到」的连接生效）。</summary>
    private volatile bool _metaEventSeen;

    /// <summary>传输层就绪时间（生命周期宽限期计算基准）。</summary>
    private DateTime _transportReadyUtc = DateTime.MinValue;

    /// <summary>生命周期宽限期内是否已降级判定为已连接（避免重复记日志）。</summary>
    private volatile bool _gracePromoted;

    public NapCatWsClient(NapCatWsClientOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    /// <summary>反向监听实际绑定端口（配置 0 = 系统分配；未监听时为 null）。</summary>
    public int? ListeningPort { get; private set; }

    /// <summary>分发事件透传：(t, 规范化后的官方事件 JSON)。</summary>
    public event EventHandler<(string Type, string Data)>? DispatchReceived;

    /// <summary>消息撤回（notice.group_recall / friend_recall 上报）。</summary>
    public event EventHandler<MessageRecallEvent>? MessageRecalled;

    public event EventHandler<ConnectionStatus>? ConnectionStateChanged;

    /// <summary>
    /// 经当前连接调用一次 NapCat（OneBot 11）API：发送 {action, params, echo}，回包由接收循环按 echo 路由。
    /// 无可用连接 / 超时 / 协议端返回非 0 retcode 一律返回 null（不抛异常、不断连接）。
    /// </summary>
    public async Task<JsonElement?> CallApiAsync(
        string action, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct = default)
    {
        var socket = _activeSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            _logger?.LogDebug("NapCat API 调用跳过（当前无可用连接）：{Action}", action);
            return null;
        }

        try
        {
            return await CallApiOnSocketAsync(socket, action, parameters, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("NapCat API 调用取消：{Action}", action);
            return null;
        }
        catch (Exception ex)
        {
            // API 调用失败绝不影响连接与接收循环
            _logger?.LogWarning(ex, "NapCat API 调用失败：{Action}", action);
            return null;
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runLoop is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_runLoop is not null)
            {
                await _runLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }

        _runLoop = null;
    }

    /// <summary>手动重连：取消当前循环，StartAsync 后以新的选项快照重启。</summary>
    public async Task ReconnectAsync()
    {
        _logger?.LogInformation("NapCat 客户端手动重连请求");
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    private void SetStatus(ConnectionStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        _logger?.LogDebug("NapCat 连接状态：{Status}", status);
        ConnectionStateChanged?.Invoke(this, status);
    }

    /// <summary>连接异常是否为鉴权失败（401/403 或不含 WebSocket 握手应答）。</summary>
    internal static bool IsAuthenticationFailure(WebSocketException ex)
    {
        if (ex.InnerException is HttpRequestException http
            && http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        // 部分 .NET/系统组合只给出 NotAWebSocket + 文本（如 "The server returned status code '401'"）
        return ex.WebSocketErrorCode == WebSocketError.NotAWebSocket
            && (ex.Message.Contains("401", StringComparison.Ordinal)
                || ex.Message.Contains("403", StringComparison.Ordinal));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var useForward = !string.IsNullOrWhiteSpace(_options.WsUrl);
        var delayMs = _options.ReconnectInitialDelayMs;
        SetStatus(ConnectionStatus.Connecting);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (useForward)
                {
                    await ConnectForwardOnceAsync(ct).ConfigureAwait(false);
                    delayMs = _options.ReconnectInitialDelayMs; // 连接曾成功建立，重置退避
                }
                else if (_options.ReverseListenPort >= 0)
                {
                    await RunReverseServerAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    _logger?.LogError(
                        "NapCat 模式未配置连接方式：请在设置页填写正向 WS 地址，或启用反向 WS 监听端口");
                    SetStatus(ConnectionStatus.Faulted);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SetStatus(ConnectionStatus.Disconnected);
                break;
            }
            catch (WebSocketException ex) when (IsAuthenticationFailure(ex))
            {
                // 鉴权失败不是抖动：明确报出（用户需核对设置页 token 与 NapCat 配置），
                // 仍按退避重试，改配置后无需重启插件即可自动恢复。
                _logger?.LogError(ex,
                    "NapCat 连接鉴权失败（access token 不匹配 / 401 / 403），请核对设置页 access token 与 NapCat 配置是否一致");
                SetStatus(ConnectionStatus.AuthenticationFailed);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "NapCat WebSocket 连接异常断开，{Delay}ms 后重试", delayMs);
                SetStatus(ConnectionStatus.Reconnecting);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delayMs = (int)Math.Min(delayMs * _options.ReconnectBackoffFactor, _options.ReconnectMaxDelayMs);
            SetStatus(ConnectionStatus.Reconnecting);
        }
    }

    // ================= 正向 WS =================

    /// <summary>连接一次 NapCat 正向 WS 并驻留接收，直到连接断开或取消。</summary>
    private async Task ConnectForwardOnceAsync(CancellationToken ct)
    {
        SetStatus(ConnectionStatus.Connecting);
        var uri = new Uri(_options.WsUrl);
        _logger?.LogInformation("连接 NapCat 正向 WS：{Url}", _options.WsUrl);

        var socket = _options.SocketFactory is not null
            ? await _options.SocketFactory(uri, ct).ConfigureAwait(false)
            : await ConnectClientWebSocketAsync(uri, ct).ConfigureAwait(false);

        using (socket)
        {
            // 传输层就绪 ≠ 已连接：先置 Authenticating，等 OneBot 生命周期/心跳事件确认
            OnTransportReady(socket, ct);
            try
            {
                await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            }
            finally
            {
                OnTransportClosed(socket);
            }
        }
    }

    /// <summary>传输层就绪（握手完成）：置「等待 OneBot 握手」状态并启动生命周期/心跳监视。</summary>
    private void OnTransportReady(WebSocket socket, CancellationToken ct)
    {
        _activeSocket = socket;
        _transportReadyUtc = DateTime.UtcNow;
        _lastMetaEventUtc = DateTime.MinValue;
        _metaEventSeen = false;
        _gracePromoted = false;
        SetStatus(ConnectionStatus.Authenticating);
        _ = WatchLifecycleAsync(socket, ct);
    }

    /// <summary>
    /// 连接关闭：清理活跃连接引用并终结在途 API 调用。
    /// <para>
    /// 同时把状态从「已连接 / 等待握手」迁出——否则反向 WS 模式下接收循环结束后，
    /// 服务端循环阻塞在下一次 accept，状态会一直停留在 <see cref="ConnectionStatus.Connected"/>，
    /// UI 显示与真实连接不一致（用户反馈的 connect 状态识别问题）。置
    /// <see cref="ConnectionStatus.Reconnecting"/> 表示「连接已断、等待重新接入/退避重试」；
    /// 停止/重连过程中的终态由外层循环负责，此处不覆盖。
    /// </para>
    /// </summary>
    private void OnTransportClosed(WebSocket socket)
    {
        var wasActive = ReferenceEquals(_activeSocket, socket);
        if (wasActive)
        {
            _activeSocket = null;
        }

        FailPendingApiCalls("连接断开");

        // 非活跃连接（已被新连接顶替）或正在停止：状态由外层循环负责（Disconnected/Reconnecting）
        if (!wasActive || _cts is null || _cts.IsCancellationRequested)
        {
            return;
        }

        if (Status is ConnectionStatus.Connected or ConnectionStatus.Authenticating)
        {
            SetStatus(ConnectionStatus.Reconnecting);
        }
    }

    /// <summary>
    /// 生命周期/心跳监视（每连接一个）：
    /// ① 收到过 meta_event 后，静默超过 <see cref="HeartbeatSilenceTimeout"/> → 判定僵死并主动断开重连；
    /// ② 从未收到 meta_event 且超过 <see cref="LifecycleGraceTimeout"/> → 记警告并降级判定为已连接
    /// （避免 NapCat 关闭心跳上报时状态永远停在「等待握手」；降级事实写入日志，可经排错面板发现）。
    /// </summary>
    private async Task WatchLifecycleAsync(WebSocket socket, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open || !ReferenceEquals(_activeSocket, socket))
                {
                    return;
                }

                if (_metaEventSeen)
                {
                    if (DateTime.UtcNow - _lastMetaEventUtc > HeartbeatSilenceTimeout)
                    {
                        _logger?.LogWarning(
                            "NapCat 已 {Seconds} 秒未收到心跳/生命周期事件，判定连接僵死并主动重连",
                            HeartbeatSilenceTimeout.TotalSeconds);
                        SetStatus(ConnectionStatus.Reconnecting);
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "heartbeat timeout", ct)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // 关闭失败：接收循环会因 socket 状态变化退出，交由重连循环处理
                        }

                        return;
                    }

                    continue;
                }

                if (!_gracePromoted && DateTime.UtcNow - _transportReadyUtc > LifecycleGraceTimeout)
                {
                    _gracePromoted = true;
                    _logger?.LogWarning(
                        "NapCat 连接就绪 {Seconds} 秒仍未收到生命周期/心跳事件，按传输层就绪降级判定为已连接（请检查 NapCat 心跳上报配置）",
                        LifecycleGraceTimeout.TotalSeconds);
                    SetStatus(ConnectionStatus.Connected);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止/重连
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "NapCat 生命周期监视异常退出（不影响接收循环）");
        }
    }

    private async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        if (!string.IsNullOrEmpty(_options.AccessTokenPlain))
        {
            ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.AccessTokenPlain}");
        }

        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }

    // ================= 反向 WS（本插件为服务端） =================

    /// <summary>反向 WS 服务端主循环：监听端口，NapCat 接入后驻留接收（支持重连与多连接）。</summary>
    private async Task RunReverseServerAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _options.ReverseListenPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _logger?.LogInformation("NapCat 反向 WS 监听已启动：ws://127.0.0.1:{Port}（等待 NapCat 接入）", ListeningPort);

        try
        {
            var handlers = new List<Task>();
            while (!ct.IsCancellationRequested)
            {
                SetStatus(ConnectionStatus.Connecting); // 监听中（等待 NapCat 接入）
                Socket client;
                try
                {
                    client = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _logger?.LogWarning(ex, "反向 WS 接受连接失败，继续监听");
                    continue;
                }

                handlers.Add(HandleReverseClientAsync(client, ct));
                handlers.RemoveAll(h => h.IsCompleted);
            }

            // 关停阶段：等待存量连接退出
            await Task.WhenAll(handlers.ToArray()).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
            ListeningPort = null;
            SetStatus(ConnectionStatus.Disconnected);
        }
    }

    /// <summary>
    /// 处理一个反向 WS 客户端连接：读 HTTP 升级请求 → token 校验（失败 401 拒绝）→
    /// 应答 101 握手 → WebSocket.CreateFromStream → 驻留接收。
    /// </summary>
    private async Task HandleReverseClientAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = new NetworkStream(client, ownsSocket: false);

                var request = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null)
                {
                    _logger?.LogWarning("反向 WS：请求头读取失败（非 HTTP 升级请求？），关闭连接");
                    return;
                }

                if (!ValidateToken(request))
                {
                    _logger?.LogWarning("反向 WS：access token 校验失败，拒绝连接（401）");
                    SetStatus(ConnectionStatus.AuthenticationFailed);
                    await WriteRawAsync(stream, "HTTP/1.1 401 Unauthorized\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", ct)
                        .ConfigureAwait(false);
                    return;
                }

                var acceptKey = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(request.SecWebSocketKey + WsMagicGuid)));
                var response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    $"Connection: {request.ConnectionValue}\r\n" +
                    $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                    "\r\n";
                await WriteRawAsync(stream, response, ct).ConfigureAwait(false);

                // WebSocket 仅实现 IDisposable（无 DisposeAsync），此处必须用同步 using
                using var ws = WebSocket.CreateFromStream(
                    stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                _logger?.LogInformation("反向 WS：NapCat 已接入（{Remote}）", client.RemoteEndPoint);
                OnTransportReady(ws, ct);
                try
                {
                    await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                }
                finally
                {
                    OnTransportClosed(ws);
                }

                _logger?.LogInformation("反向 WS：NapCat 连接断开，等待重新接入");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "反向 WS 客户端连接处理异常（不影响继续监听）");
            }
        }
    }

    private sealed record ReverseRequest(string Path, string Query, string ConnectionValue, string Authorization, string SecWebSocketKey);

    private static async Task<ReverseRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var received = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (received == 0)
            {
                return null;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, received));
            if (sb.ToString().Contains("\r\n\r\n"))
            {
                break;
            }

            if (sb.Length > 16 * 1024)
            {
                return null; // 请求头异常过大，防御性断开
            }
        }

        var lines = sb.ToString().Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2 || !requestLine[0].StartsWith("GET", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pathAndQuery = requestLine[1];
        var path = pathAndQuery.Split('?')[0];
        var query = pathAndQuery.Contains('?') ? pathAndQuery[(pathAndQuery.IndexOf('?') + 1)..] : "";
        var connectionValue = "";
        var authorization = "";
        var secKey = "";
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                connectionValue = value;
            }
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = value;
            }
            else if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                secKey = value;
            }
        }

        return string.IsNullOrEmpty(secKey)
            ? null
            : new ReverseRequest(path, query, connectionValue, authorization, secKey);
    }

    /// <summary>token 校验：未配置 token 时放行；配置后校验查询参数 access_token 或 Authorization: Bearer。</summary>
    private bool ValidateToken(ReverseRequest request)
    {
        if (string.IsNullOrEmpty(_options.AccessTokenPlain))
        {
            return true;
        }

        foreach (var pair in request.Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "access_token" &&
                Uri.UnescapeDataString(kv[1]) == _options.AccessTokenPlain)
            {
                return true;
            }
        }

        const string prefix = "Bearer ";
        if (request.Authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            request.Authorization[prefix.Length..] == _options.AccessTokenPlain)
        {
            return true;
        }

        return false;
    }

    private static async Task WriteRawAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ================= API 调用与文件直链解析 =================

    /// <summary>
    /// 经当前连接调用一次 NapCat（OneBot 11）API：发送 {action, params, echo}，
    /// 回包由接收循环按 echo 路由完成。超时/失败返回 null（不抛异常、不断连接）。
    /// </summary>
    private async Task<JsonElement?> CallApiOnSocketAsync(
        WebSocket socket, string action, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var echo = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingApiCalls)
        {
            _pendingApiCalls[echo] = tcs;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["action"] = action,
                ["params"] = parameters,
                ["echo"] = echo
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task.WaitAsync(ApiCallTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning("NapCat API 调用超时（{Timeout}秒）：{Action}", ApiCallTimeout.TotalSeconds, action);
            return null;
        }
        finally
        {
            lock (_pendingApiCalls)
            {
                _pendingApiCalls.Remove(echo);
            }
        }
    }

    /// <summary>echo 回包路由：匹配在途 API 调用则完成其等待并返回 true（帧已消费）。</summary>
    private bool TryRouteApiEcho(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("echo", out var echoEl)
                || echoEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var echo = echoEl.GetString() ?? "";
            TaskCompletionSource<JsonElement?>? tcs;
            lock (_pendingApiCalls)
            {
                _pendingApiCalls.TryGetValue(echo, out tcs);
            }

            if (tcs is null)
            {
                return false; // 非本客户端在途调用（如 NapCat 正向 WS 的 get_status 回显）：交给规范化器忽略
            }

            var retcode = root.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number
                && rc.TryGetInt64(out var code)
                    ? code
                    : 0;
            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null
                ? d.Clone()
                : null;
            tcs.TrySetResult(retcode == 0 ? data : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>分发前解析 file 段直链：失败按事件自带 JSON 原样分发（文件管道缺失地址语义兜底）。
    /// 作为独立异步任务运行——回包依赖接收循环，绝不能内联阻塞接收。</summary>
    private async Task DispatchWithFileResolutionAsync(
        WebSocket socket, NapCatEventResult result, CancellationToken ct)
    {
        try
        {
            var data = await NapCatFileUrlResolver.EnrichAsync(result.MappedJson,
                (action, parameters, token) => CallApiOnSocketAsync(socket, action, parameters, token),
                _logger, ct).ConfigureAwait(false);
            DispatchReceived?.Invoke(this, (result.DispatchType, data));
        }
        catch (OperationCanceledException)
        {
            // 客户端停止/重连：不再分发
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NapCat 群文件直链解析异常，按事件自带 URL 分发");
            DispatchReceived?.Invoke(this, (result.DispatchType, result.MappedJson));
        }
    }

    /// <summary>连接断开时终结所有在途 API 调用（结果为 null → 直链解析按事件自带 URL 处理）。</summary>
    private void FailPendingApiCalls(string reason)
    {
        lock (_pendingApiCalls)
        {
            foreach (var tcs in _pendingApiCalls.Values)
            {
                tcs.TrySetResult(null);
            }

            _pendingApiCalls.Clear();
        }

        _logger?.LogDebug("NapCat {Reason}，在途 API 调用已按失败处理", reason);
    }

    // ================= 接收与分发 =================

    /// <summary>接收主循环：echo 回包路由 → OneBot 事件解析 → 规范化（file 段交异步任务解析直链）→ 分发；返回即表示连接已断开。</summary>
    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
            if (text.Length == 0)
            {
                continue;
            }

            try
            {
                if (TryRouteApiEcho(text))
                {
                    continue;
                }

                // 生命周期/心跳：状态机从「等待 OneBot 握手」升级为「已连接」的唯一依据。
                // 握手成功只代表管道通了，不能据此判定协议端可用（旧缺陷根因）。
                if (NapCatEventNormalizer.IsLifecycleOrHeartbeat(text))
                {
                    _lastMetaEventUtc = DateTime.UtcNow;
                    if (!_metaEventSeen)
                    {
                        _metaEventSeen = true;
                        _logger?.LogInformation("NapCat 已收到生命周期/心跳事件，连接确认可用");
                    }

                    SetStatus(ConnectionStatus.Connected);
                    continue;
                }

                var result = NapCatEventNormalizer.Normalize(text);
                if (!result.Handled)
                {
                    _logger?.LogDebug("NapCat 事件忽略：{Reason}", result.IgnoreReason);
                    continue;
                }

                // 撤回上报（notice.group_recall / friend_recall）：走独立通道联动删除存档，不进消息管道
                if (result.Recall is not null)
                {
                    _logger?.LogInformation(
                        "NapCat 撤回上报：MessageId={MessageId}, Group={Group}, Operator={Operator}",
                        result.Recall.MessageId, result.Recall.GroupOpenId, result.Recall.OperatorOpenId);
                    MessageRecalled?.Invoke(this, result.Recall);
                    continue;
                }

                if (result.HasFileAttachment)
                {
                    // 独立异步任务：解析需要等回包，回包需要本循环继续接收（禁止内联等待造成死锁）
                    _ = DispatchWithFileResolutionAsync(socket, result, ct);
                }
                else
                {
                    DispatchReceived?.Invoke(this, (result.DispatchType, result.MappedJson));
                }
            }
            catch (Exception ex)
            {
                // 单条事件解析/分发失败不影响接收循环
                _logger?.LogError(ex, "NapCat 事件处理失败：{Snippet}", text.Length <= 200 ? text : text[..200]);
            }
        }
    }

    /// <summary>接收一条完整文本帧（处理分片）。</summary>
    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return "";
            }

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
