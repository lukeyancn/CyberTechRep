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
/// <item>事件处理：meta_event（心跳/生命周期）/echo 回包忽略；post_type=message 事件经
/// <see cref="NapCatEventNormalizer"/> 规范化为官方事件 JSON 后经 DispatchReceived 下发，
/// 接入管道（白名单/幂等）零改动复用。</item>
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

    public event EventHandler<ConnectionStatus>? ConnectionStateChanged;

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
            SetStatus(ConnectionStatus.Connected);
            await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
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
                SetStatus(ConnectionStatus.Connected);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
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

    // ================= 接收与分发 =================

    /// <summary>接收主循环：OneBot 事件解析 → 规范化 → 分发；返回即表示连接已断开。</summary>
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
                var result = NapCatEventNormalizer.Normalize(text);
                if (result.Handled)
                {
                    DispatchReceived?.Invoke(this, (result.DispatchType, result.MappedJson));
                }
                else
                {
                    _logger?.LogDebug("NapCat 事件忽略：{Reason}", result.IgnoreReason);
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
