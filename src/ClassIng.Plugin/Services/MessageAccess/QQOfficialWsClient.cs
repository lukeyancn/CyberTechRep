using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ClassIng.Plugin.Services.MessageAccess;

/// <summary>WebSocket 客户端运行参数。</summary>
public sealed class QQOfficialWsClientOptions
{
    public string AppId { get; set; } = "";

    /// <summary>AppSecret 明文（由设置服务解密后传入；日志中永不出现在）。</summary>
    public string AppSecretPlain { get; set; } = "";

    public string ApiBase { get; set; } = "https://api.sgroup.qq.com";

    public string TokenApiUrl { get; set; } = "https://bots.qq.com/app/getAppAccessToken";

    /// <summary>GROUP_AND_C2C_EVENT Intent（1 &lt;&lt; 25）。</summary>
    public int Intents { get; set; } = 1 << 25;

    public int ReconnectInitialDelayMs { get; set; } = 2000;

    public double ReconnectBackoffFactor { get; set; } = 2.0;

    public int ReconnectMaxDelayMs { get; set; } = 300_000;

    /// <summary>WebSocket 连接工厂（默认 ClientWebSocket；单元测试注入本地测试服务器）。</summary>
    public Func<Uri, CancellationToken, Task<WebSocket>>? SocketFactory { get; set; }

    /// <summary>HTTP 调用器（默认内部 HttpClient；单元测试注入本地测试服务器）。</summary>
    public HttpMessageInvoker? HttpInvoker { get; set; }
}

/// <summary>
/// QQ 官方机器人开放平台 WebSocket 网关客户端（自研薄封装，约数百行）。
/// 职责：AccessToken 获取与刷新、网关获取、Identify/Resume、心跳、服务端重连指令处理、指数退避重连。
/// 事件分发解析交给 <see cref="MessageIngestPipeline"/>，本类只透传 (t, d)。
/// </summary>
public sealed class QQOfficialWsClient : IAsyncDisposable
{
    private readonly QQOfficialWsClientOptions _options;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private volatile bool _disposed;

    // --- 会话状态（用于 Resume）---
    private string? _sessionId;
    private int _lastSeq;

    // --- AccessToken 缓存 ---
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    /// <summary>分发事件透传：(t, d 原始 JSON 文本)。</summary>
    public event EventHandler<(string Type, string Data)>? DispatchReceived;

    public event EventHandler<ConnectionStatus>? ConnectionStateChanged;

    public QQOfficialWsClient(QQOfficialWsClientOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    /// <summary>启动客户端（非阻塞；内部长循环，随 StopAsync/Dispose 结束）。</summary>
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

    /// <summary>手动重连：取消当前连接，主循环将以短退避立即重试。</summary>
    public async Task ReconnectAsync()
    {
        _logger?.LogInformation("手动重连请求：关闭当前 WebSocket 连接");
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
        _logger?.LogDebug("连接状态：{Status}", status);
        ConnectionStateChanged?.Invoke(this, status);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delayMs = _options.ReconnectInitialDelayMs;
        SetStatus(ConnectionStatus.Connecting);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
                delayMs = _options.ReconnectInitialDelayMs; // 连接曾成功建立，重置退避
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SetStatus(ConnectionStatus.Disconnected);
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "WebSocket 连接异常断开，{Delay}ms 后重试", delayMs);
                SetStatus(ConnectionStatus.Reconnecting);
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

    /// <summary>建立一次连接并驻留接收，直到连接断开或取消。</summary>
    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        SetStatus(ConnectionStatus.Connecting);

        var token = await EnsureTokenAsync(ct).ConfigureAwait(false);
        var gatewayUrl = await GetGatewayUrlAsync(token, ct).ConfigureAwait(false);
        _logger?.LogInformation("连接 WebSocket 网关：{Url}", gatewayUrl);

        var socket = _options.SocketFactory is not null
            ? await _options.SocketFactory(new Uri(gatewayUrl), ct).ConfigureAwait(false)
            : await ConnectClientWebSocketAsync(new Uri(gatewayUrl), ct).ConfigureAwait(false);

        using (socket)
        {
            SetStatus(ConnectionStatus.Connected);
            var sessionResumable = _sessionId is not null;
            await ReceiveLoopAsync(socket, token, sessionResumable, ct).ConfigureAwait(false);
        }
    }

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }

    /// <summary>接收主循环：帧解析 → op 分派；返回即表示连接已断开。</summary>
    private async Task ReceiveLoopAsync(WebSocket socket, string token, bool tryResume, CancellationToken ct)
    {
        var heartbeatTask = Task.CompletedTask;
        try
        {
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var identified = false;

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var op = root.TryGetProperty("op", out var opEl) && opEl.TryGetInt32(out var o) ? o : -1;
                var seq = root.TryGetProperty("s", out var sEl) && sEl.TryGetInt32(out var s) ? s : _lastSeq;
                if (root.TryGetProperty("s", out _) && seq > 0)
                {
                    _lastSeq = seq;
                }

                switch (op)
                {
                    case GatewayOp.Hello:
                    {
                        var interval = 30_000;
                        if (root.TryGetProperty("d", out var d) &&
                            d.TryGetProperty("heartbeat_interval", out var h) && h.TryGetInt32(out var i))
                        {
                            interval = i;
                        }

                        _logger?.LogInformation("收到 Hello，心跳间隔 {Interval}ms，发送 {Mode}",
                            interval, tryResume && _sessionId is not null ? "Resume" : "Identify");

                        heartbeatTask = HeartbeatLoopAsync(socket, interval, ct);

                        if (tryResume && _sessionId is not null)
                        {
                            await SendJsonAsync(socket, new
                            {
                                op = GatewayOp.Resume,
                                d = new { token = $"QQBot {token}", session_id = _sessionId, seq = _lastSeq }
                            }, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await SendJsonAsync(socket, new
                            {
                                op = GatewayOp.Identify,
                                d = new { token = $"QQBot {token}", intents = _options.Intents, shard = new[] { 0, 1 } }
                            }, ct).ConfigureAwait(false);
                            identified = true;
                        }

                        break;
                    }

                    case GatewayOp.Dispatch:
                    {
                        var t = root.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.String
                            ? tEl.GetString() ?? ""
                            : "";
                        if (t == "READY")
                        {
                            if (root.TryGetProperty("d", out var rd) &&
                                rd.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                            {
                                _sessionId = sid.GetString();
                                _logger?.LogInformation("会话建立（READY）：session_id={SessionId}", _sessionId);
                            }

                            helloCts.Cancel(); // 已完成鉴权，取消握手超时
                        }
                        else if (t.Length > 0)
                        {
                            var dJson = root.TryGetProperty("d", out var dd) ? dd.GetRawText() : "{}";
                            DispatchReceived?.Invoke(this, (t, dJson));
                        }

                        break;
                    }

                    case GatewayOp.HeartbeatAck:
                        // 心跳确认（当前实现不追踪 missed-ack，靠连接异常触发重连）
                        break;

                    case GatewayOp.ServerReconnect:
                        _logger?.LogInformation("服务端要求重连（op=7）：关闭并恢复会话");
                        await CloseQuietlyAsync(socket, ct).ConfigureAwait(false);
                        return; // 保留 _sessionId/_lastSeq，外层循环将以 Resume 重连

                    case GatewayOp.InvalidSession:
                        _logger?.LogWarning("会话失效（op=9）：丢弃会话，重新 Identify");
                        _sessionId = null;
                        _lastSeq = 0;
                        if (!identified)
                        {
                            await SendJsonAsync(socket, new
                            {
                                op = GatewayOp.Identify,
                                d = new { token = $"QQBot {token}", intents = _options.Intents, shard = new[] { 0, 1 } }
                            }, ct).ConfigureAwait(false);
                            identified = true;
                        }

                        break;

                    default:
                        _logger?.LogDebug("忽略未知 op={Op}", op);
                        break;
                }
            }
        }
        finally
        {
            await CloseQuietlyAsync(socket, ct).ConfigureAwait(false);
        }
    }

    /// <summary>心跳循环：按 hello 下发的间隔发送 op=1（携带最新 seq）。</summary>
    private async Task HeartbeatLoopAsync(WebSocket socket, int intervalMs, CancellationToken ct)
    {
        try
        {
            // 首跳立即发送（协议要求连接后尽快开始心跳）
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await SendJsonAsync(socket, new { op = GatewayOp.Heartbeat, d = _lastSeq }, ct).ConfigureAwait(false);
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (WebSocketException)
        {
            // 连接已断：外层接收循环负责重连
        }
    }

    // ---------- HTTP：AccessToken 与网关 ----------

    /// <summary>获取缓存的 AccessToken（过期前 5 分钟自动刷新）。</summary>
    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _accessToken;
        }

        var invoker = _options.HttpInvoker ?? CreateDefaultHttpInvoker();
        var url = _options.TokenApiUrl;
        _logger?.LogInformation("请求 AccessToken：{Url}（AppId={AppId}）", url, _options.AppId);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        // QQ 官方接口要求 JSON 请求体；用表单格式会返回误导性的 {"code":100007,"message":"appid invalid"}
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { appId = _options.AppId, clientSecret = _options.AppSecretPlain }),
            Encoding.UTF8,
            "application/json");

        // 无整体超时（HttpMessageInvoker 不带默认超时），防止代理黑洞时启动循环永久挂起
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var resp = await invoker.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 脱敏：只输出状态码，不输出响应体（可能回显敏感信息）
            throw new InvalidOperationException($"AccessToken 获取失败：HTTP {(int)resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        var expiresIn = 7200;
        if (doc.RootElement.TryGetProperty("expires_in", out var ei))
        {
            // 官方响应 expires_in 可能为字符串，容错解析
            expiresIn = ei.ValueKind == JsonValueKind.String && int.TryParse(ei.GetString(), out var e1) ? e1
                : ei.TryGetInt32(out var e2) ? e2 : 7200;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("AccessToken 响应缺少 access_token 字段");
        }

        _accessToken = token;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn - 300, 60));
        _logger?.LogInformation("AccessToken 获取成功，有效期 {Seconds}s（提前 5 分钟刷新）", expiresIn);
        return token;
    }

    /// <summary>获取 WebSocket 网关地址。</summary>
    private async Task<string> GetGatewayUrlAsync(string token, CancellationToken ct)
    {
        var invoker = _options.HttpInvoker ?? CreateDefaultHttpInvoker();
        var url = $"{_options.ApiBase.TrimEnd('/')}/gateway";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var resp = await invoker.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"网关地址获取失败：HTTP {(int)resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var wsUrl = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(wsUrl))
        {
            throw new InvalidOperationException("网关响应缺少 url 字段");
        }

        return wsUrl!;
    }

    private static HttpMessageInvoker CreateDefaultHttpInvoker()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        return new HttpMessageInvoker(handler);
    }

    // ---------- WebSocket 收发工具 ----------

    private async Task SendJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
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
                throw new WebSocketException("服务端发送 Close 帧");
            }

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, CancellationToken ct)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 尽力而为
        }
        finally
        {
            if (socket is ClientWebSocket cws)
            {
                cws.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}
