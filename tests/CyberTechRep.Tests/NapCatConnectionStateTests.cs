using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 7 状态机回归：连接状态必须以 OneBot 生命周期/心跳事件为准，且断开后不得停留在「已连接」。
/// <para>
/// 覆盖四条路径：① 握手完成只置 <see cref="ConnectionStatus.Authenticating"/>（传输层就绪 ≠ 已连接）；
/// ② 收到 <c>meta_event</c>（lifecycle / heartbeat）才置 <see cref="ConnectionStatus.Connected"/>；
/// ③ 连接断开后状态必须离开 <see cref="ConnectionStatus.Connected"/>（否则 UI 显示与实际不符）；
/// ④ token 校验失败置 <see cref="ConnectionStatus.AuthenticationFailed"/>，并覆盖异常分类纯逻辑。
/// </para>
/// </summary>
public sealed class NapCatConnectionStateTests
{
    private readonly ITestOutputHelper _output;

    public NapCatConnectionStateTests(ITestOutputHelper output) => _output = output;

    /// <summary>启动反向监听（端口 0 = 系统分配）的客户端，返回 (client, 实际端口)。</summary>
    private async Task<(NapCatWsClient Client, int Port)> StartReverseAsync(string token = "")
    {
        var client = new NapCatWsClient(new NapCatWsClientOptions
        {
            WsUrl = "",
            ReverseListenPort = 0,
            AccessTokenPlain = token
        }, new XunitLogger(_output));
        await client.StartAsync();
        for (var i = 0; i < 50 && client.ListeningPort is null; i++)
        {
            await Task.Delay(100);
        }

        Assert.NotNull(client.ListeningPort);
        return (client, client.ListeningPort!.Value);
    }

    /// <summary>轮询等待状态（最长 5 秒）。</summary>
    private static async Task<bool> WaitForStatusAsync(NapCatWsClient client, ConnectionStatus expected)
    {
        for (var i = 0; i < 50; i++)
        {
            if (client.Status == expected)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task SendAsync(ClientWebSocket ws, string json)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true,
            CancellationToken.None);

    [Fact]
    public async Task 握手完成_只置Authenticating_收到生命周期事件才置Connected()
    {
        var (client, port) = await StartReverseAsync();
        var transitions = new List<ConnectionStatus>();
        client.ConnectionStateChanged += (_, s) =>
        {
            lock (transitions)
            {
                transitions.Add(s);
            }
        };

        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

            Assert.True(await WaitForStatusAsync(client, ConnectionStatus.Authenticating),
                $"握手后应进入 Authenticating，实际：{client.Status}");

            // 传输层就绪 ≠ 已连接：没有任何 OneBot 事件时保持 Authenticating
            await Task.Delay(300);
            Assert.Equal(ConnectionStatus.Authenticating, client.Status);

            await SendAsync(ws,
                """{ "post_type": "meta_event", "meta_event_type": "lifecycle", "sub_type": "connect", "time": 1725710000 }""");

            Assert.True(await WaitForStatusAsync(client, ConnectionStatus.Connected),
                $"收到 lifecycle 后应判定已连接，实际：{client.Status}");

            lock (transitions)
            {
                Assert.Equal(
                    new[] { ConnectionStatus.Authenticating, ConnectionStatus.Connected },
                    transitions.ToArray());
            }
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task 收到心跳事件_同样判定为已连接()
    {
        var (client, port) = await StartReverseAsync();
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
            Assert.True(await WaitForStatusAsync(client, ConnectionStatus.Authenticating));

            await SendAsync(ws,
                """{ "post_type": "meta_event", "meta_event_type": "heartbeat", "status": {}, "interval": 5000, "time": 1725710005 }""");

            Assert.True(await WaitForStatusAsync(client, ConnectionStatus.Connected),
                $"收到心跳后应判定已连接，实际：{client.Status}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task 连接断开_状态必须离开Connected()
    {
        // 断开后若仍显示「已连接」，UI 状态与真实连接不一致（用户反馈的 connect 状态识别问题）
        var (client, port) = await StartReverseAsync();
        try
        {
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
                Assert.True(await WaitForStatusAsync(client, ConnectionStatus.Authenticating));

                await SendAsync(ws,
                    """{ "post_type": "meta_event", "meta_event_type": "lifecycle", "sub_type": "connect" }""");
                Assert.True(await WaitForStatusAsync(client, ConnectionStatus.Connected));

                // 模拟 NapCat 进程被杀/网络中断：直接 abort，不走关闭握手
                ws.Abort();
            }

            var leftConnected = false;
            for (var i = 0; i < 50; i++)
            {
                if (client.Status != ConnectionStatus.Connected)
                {
                    leftConnected = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(leftConnected, $"连接已断开，状态仍停留在 {client.Status}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task token不匹配_状态置AuthenticationFailed()
    {
        var (client, port) = await StartReverseAsync(token: "secret-token");
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            var request =
                "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            await stream.FlushAsync();

            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer);

            Assert.True(await WaitForStatusAsync(client, ConnectionStatus.AuthenticationFailed),
                $"token 不匹配应置 AuthenticationFailed，实际：{client.Status}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ================= 异常分类纯逻辑 =================

    [Fact]
    public void IsAuthenticationFailure_401与403判定为鉴权失败()
    {
        Assert.True(NapCatWsClient.IsAuthenticationFailure(
            new WebSocketException(WebSocketError.NotAWebSocket, "The server returned status code '401'")));
        Assert.True(NapCatWsClient.IsAuthenticationFailure(
            new WebSocketException(WebSocketError.NotAWebSocket, "The server returned status code '403'")));
    }

    [Fact]
    public void IsAuthenticationFailure_内层Http异常带状态码_判定为鉴权失败()
    {
        var unauthorized = new WebSocketException(
            WebSocketError.Faulted,
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized));
        var forbidden = new WebSocketException(
            WebSocketError.Faulted,
            new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden));

        Assert.True(NapCatWsClient.IsAuthenticationFailure(unauthorized));
        Assert.True(NapCatWsClient.IsAuthenticationFailure(forbidden));
    }

    [Fact]
    public void IsAuthenticationFailure_普通网络抖动_不判定为鉴权失败()
    {
        Assert.False(NapCatWsClient.IsAuthenticationFailure(
            new WebSocketException(WebSocketError.ConnectionClosedPrematurely)));
        Assert.False(NapCatWsClient.IsAuthenticationFailure(
            new WebSocketException(WebSocketError.NotAWebSocket, "The server returned status code '502'")));
        Assert.False(NapCatWsClient.IsAuthenticationFailure(
            new WebSocketException(WebSocketError.Faulted, "401 出现在非握手错误里也不应误判")));
    }
}
