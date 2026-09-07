using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using CyberTechRep.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 模块 1 集成测试：官方网关协议（鉴权/心跳/断线重连/会话恢复）与接入服务联动。
/// 使用本地 TcpListener 模拟网关 + 假 HTTP 处理器（AccessToken/网关地址）。
/// </summary>
public class MessageIngestServiceTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dataDir;
    private TestGatewayServer _server = null!;
    private readonly List<MessageRecord> _received = [];
    private readonly ConcurrentQueue<ConnectionStatus> _statusLog = new();

    public MessageIngestServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        _server = new TestGatewayServer { OnConnection = (_, _) => Task.CompletedTask };
        await _server.StartAsync();
    }

    /// <summary>构造指向本地假网关的接入服务。</summary>
    private MessageIngestService CreateService()
    {
        var port = _server.Port;
        var service = new MessageIngestService(
            new IngestOptionsProvider
            {
                GetSettings = () => new ConnectionSettings
                {
                    AppId = "app-test",
                    AppSecretProtected = "secret-plain",     // 测试用直传明文（SecretUnprotector 原样返回）
                    ApiBase = "http://fake.api",
                    TokenApiUrl = "http://fake.api/token",
                    ReconnectInitialDelaySec = 1,
                    ReconnectBackoffFactor = 2.0,
                    ReconnectMaxDelaySec = 5
                },
                DataDirectory = _dataDir
            },
            new XunitLogger(_output),
            options =>
            {
                options.HttpInvoker = new HttpMessageInvoker(new FakePlatformHttpHandler(port));
                options.SocketFactory = async (uri, ct) =>
                {
                    var ws = new System.Net.WebSockets.ClientWebSocket();
                    await ws.ConnectAsync(uri, ct);
                    return ws;
                };
            });
        service.MessageReceived += (_, m) => _received.Add(m);
        service.StatusChanged += (_, s) => _statusLog.Enqueue(s);
        return service;
    }

    /// <summary>连接脚本：完成 Hello → 鉴权 → READY 握手，返回收到的帧队列供断言。</summary>
    internal static async Task<ConcurrentQueue<(int Op, JsonDocument Doc)>> HandshakeAsync(
        GatewayConnection conn, string sessionId, int heartbeatIntervalMs, CancellationToken ct,
        bool expectResume = false)
    {
        var frames = new ConcurrentQueue<(int, JsonDocument)>();
        await conn.SendJsonAsync(new { op = GatewayOp.Hello, d = new { heartbeat_interval = heartbeatIntervalMs } }, ct);

        while (true)
        {
            var text = await conn.ReadTextAsync(ct);
            if (text is null)
            {
                throw new IOException("连接在握手阶段被客户端关闭");
            }

            using var doc = JsonDocument.Parse(text);
            var op = doc.RootElement.GetProperty("op").GetInt32();
            frames.Enqueue((op, JsonDocument.Parse(text)));

            if (expectResume && op == GatewayOp.Resume)
            {
                break;
            }

            if (!expectResume && op == GatewayOp.Identify)
            {
                break;
            }
        }

        await conn.SendJsonAsync(new
        {
            op = GatewayOp.Dispatch,
            s = 1,
            t = "READY",
            d = new { session_id = sessionId, user = new { id = "u-bot" } }
        }, ct);
        return frames;
    }

    private static async Task<(int Op, JsonDocument Doc)> ReadOpAsync(
        GatewayConnection conn, CancellationToken ct)
    {
        var text = await conn.ReadTextAsync(ct);
        Assert.NotNull(text);
        var doc = JsonDocument.Parse(text!);
        return (doc.RootElement.GetProperty("op").GetInt32(), doc);
    }

    [Fact]
    public async Task 鉴权_Identify携带QQBot前缀Token与Intent_并按间隔发送心跳()
    {
        var service = CreateService();
        var connDone = new TaskCompletionSource();

        _server.OnConnection = async (conn, ct) =>
        {
            var frames = await HandshakeAsync(conn, "sess-ok", 150, ct);
            // 断言 Identify 内容
            var (op, doc) = frames.First(f => f.Item1 == GatewayOp.Identify);
            Assert.Equal(GatewayOp.Identify, op);
            var d = doc.RootElement.GetProperty("d");
            Assert.Equal("QQBot tok-test-1", d.GetProperty("token").GetString());
            Assert.Equal(1 << 25, d.GetProperty("intents").GetInt32());

            // 心跳：150ms 间隔，1.5s 内应收到至少 2 条 op=1
            var deadline = DateTime.UtcNow.AddSeconds(1.5);
            var heartbeats = 0;
            while (DateTime.UtcNow < deadline && heartbeats < 2)
            {
                var text = await conn.ReadTextAsync(ct);
                if (text is null)
                {
                    break;
                }

                using var hb = JsonDocument.Parse(text);
                if (hb.RootElement.GetProperty("op").GetInt32() == GatewayOp.Heartbeat)
                {
                    heartbeats++;
                }
            }

            Assert.True(heartbeats >= 2, $"1.5 秒内心跳数量不足：{heartbeats}");
            connDone.SetResult();
        };

        await service.StartAsync();
        await connDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionStatus.Connected, service.Status);
        await service.StopAsync();
    }

    [Fact]
    public async Task 断线后指数退避重连_恢复会话Resume_消息送达不丢失()
    {
        var service = CreateService();
        var secondConnHandshook = new TaskCompletionSource();

        _server.OnConnection = async (conn, ct) =>
        {
            if (_server.AcceptedConnections == 1)
            {
                // 第一条连接：正常握手后立即暴力断开（模拟网络故障）
                await HandshakeAsync(conn, "sess-1", 30_000, ct);
                conn.Dispose();
            }
            else
            {
                // 第二条连接：客户端应携带 Resume 恢复会话
                var frames = await HandshakeAsync(conn, "sess-1", 30_000, ct, expectResume: true);
                var (_, doc) = frames.First(f => f.Item1 == GatewayOp.Resume);
                var d = doc.RootElement.GetProperty("d");
                Assert.Equal("sess-1", d.GetProperty("session_id").GetString());   // 会话恢复
                Assert.True(d.GetProperty("seq").GetInt32() >= 1);                 // 从断点 seq 继续

                // 恢复后下发一条群消息
                await conn.SendJsonAsync(new
                {
                    op = GatewayOp.Dispatch,
                    s = 2,
                    t = GroupEventTypes.GroupMessageCreate,
                    d = new
                    {
                        id = "msg-42",
                        group_openid = "grp-1",
                        author = new { member_openid = "m-1", username = "课代表" },
                        content = "明天交数学作业P23",
                        attachments = Array.Empty<object>()
                    }
                }, ct);
                secondConnHandshook.TrySetResult();
            }
        };

        await service.StartAsync();
        await secondConnHandshook.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await TestWait.WaitForAsync(() => _received.Any(m => m.MessageId == "msg-42"),
            TimeSpan.FromSeconds(5), "恢复会话后群消息应送达");
        var record = _received.Single(m => m.MessageId == "msg-42");
        Assert.Equal("grp-1", record.GroupOpenId);
        Assert.Equal("明天交数学作业P23", record.Segments[0].Text);

        // 状态机：应观察到 Reconnecting
        Assert.Contains(ConnectionStatus.Reconnecting, _statusLog);
        await service.StopAsync();
    }

    [Fact]
    public async Task 历史补拉_官方平台无此能力_返回空并记日志()
    {
        var service = CreateService();
        await service.StartAsync();
        var history = await service.FetchHistoryAsync(3);
        Assert.Empty(history);
        await service.StopAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>假开放平台 HTTP 处理器：AccessToken 换取 + 网关地址下发（指向本地测试网关）。</summary>
public sealed class FakePlatformHttpHandler(int gatewayPort) : HttpMessageHandler
{
    private int _tokenCalls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.PathContains("/token"))
        {
            var calls = Interlocked.Increment(ref _tokenCalls);
            var json = JsonSerializer.Serialize(new { access_token = $"tok-test-{calls}", expires_in = "7200" });
            return Task.FromResult(JsonResponse(json));
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.PathContains("/gateway"))
        {
            var json = JsonSerializer.Serialize(new { url = $"ws://127.0.0.1:{gatewayPort}/ws" });
            return Task.FromResult(JsonResponse(json));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

file static class UriExtensions
{
    public static bool PathContains(this Uri uri, string segment) =>
        uri.AbsolutePath.Contains(segment, StringComparison.OrdinalIgnoreCase);
}
