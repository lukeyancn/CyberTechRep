using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// NapCat（OneBot 11）模式测试：事件规范化（群/私聊、数组/字符串 message 格式）、
/// 接入管道复用（白名单/幂等零改动）、反向 WS token 拒绝路径与一键启动失败路径。
/// </summary>
public class NapCatIngestTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dataDir;

    public NapCatIngestTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
    }

    // ================= 事件规范化 =================

    [Fact]
    public void 群消息_数组段格式_规范化为官方事件并进管道()
    {
        var raw = """
            {
              "self_id": 10001, "time": 1725710000, "post_type": "message", "message_type": "group",
              "message_id": 12345, "user_id": 666, "group_id": 888888,
              "sender": { "user_id": 666, "nickname": "课代表", "card": "数学科代表" },
              "message": [
                { "type": "text", "data": { "text": "明天交数学作业 " } },
                { "type": "at", "data": { "qq": 10001 } },
                { "type": "image", "data": { "file": "hw.png", "url": "https://example.com/hw.png" } },
                { "type": "face", "data": { "id": 1 } }
              ]
            }
            """;

        var result = NapCatEventNormalizer.Normalize(raw);

        Assert.True(result.Handled, $"reason={result.IgnoreReason}");
        Assert.Equal(GroupEventTypes.GroupMessageCreate, result.DispatchType);

        // 规范化 JSON 可被既有官方事件解析器解析（管道零改动）
        using var doc = JsonDocument.Parse(result.MappedJson);
        var ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
        Assert.Equal("12345", ev.Id);
        Assert.Equal("888888", ev.GroupOpenId);
        Assert.Equal("666", ev.MemberOpenId);
        Assert.Equal("课代表", ev.SenderNickname);
        Assert.Contains("明天交数学作业", ev.Content);
        Assert.Contains("[@10001]", ev.Content);
        Assert.Contains("[表情]", ev.Content);
        var image = Assert.Single(ev.Attachments);
        Assert.Equal("hw.png", image.FileName);
        Assert.Equal("https://example.com/hw.png", image.Url);
        Assert.StartsWith("image/", image.ContentType);

        // 全链路：官方管道（幂等/映射）按原逻辑产出 MessageRecord
        var pipeline = CreatePipeline();
        var result2 = pipeline.HandleDispatch(result.DispatchType, result.MappedJson);
        Assert.Equal(PipelineAction.Accepted, result2.Action);
        var record = Assert.Single(_received);
        Assert.Equal("12345", record.MessageId);
        Assert.Equal("888888", record.GroupOpenId);
        Assert.Contains(SegmentTypes.Text, record.Segments.Select(s => s.Type));
        Assert.Contains(SegmentTypes.Image, record.Segments.Select(s => s.Type));
    }

    [Fact]
    public void 群消息_字符串格式_整体作为纯文本()
    {
        var raw = """
            { "post_type": "message", "message_type": "group", "message_id": 777,
              "user_id": 1, "group_id": 2, "message": "明天交作业", "sender": { "nickname": "老师" } }
            """;

        var result = NapCatEventNormalizer.Normalize(raw);

        Assert.True(result.Handled, $"reason={result.IgnoreReason}");
        using var doc = JsonDocument.Parse(result.MappedJson);
        var ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
        Assert.Equal("777", ev.Id);
        Assert.Equal("2", ev.GroupOpenId);
        Assert.Equal("明天交作业", ev.Content);
        Assert.Equal("老师", ev.SenderNickname);
        Assert.Empty(ev.Attachments);
    }

    [Fact]
    public void 私聊消息_使用private占位群标识()
    {
        var raw = """
            { "post_type": "message", "message_type": "private", "message_id": 9,
              "user_id": 42, "message": "你好" }
            """;

        var result = NapCatEventNormalizer.Normalize(raw);

        Assert.True(result.Handled, $"reason={result.IgnoreReason}");
        using var doc = JsonDocument.Parse(result.MappedJson);
        var ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
        Assert.Equal("private:42", ev.GroupOpenId);
        Assert.Equal("42", ev.MemberOpenId);
        Assert.Equal("你好", ev.Content);
    }

    [Theory]
    [InlineData("""{ "post_type": "meta_event", "meta_event_type": "heartbeat", "status": {}, "interval": 5000 }""", "heartbeat")]
    [InlineData("""{ "post_type": "meta_event", "meta_event_type": "lifecycle", "sub_type": "connect" }""", "lifecycle")]
    [InlineData("""{ "status": {}, "retcode": 0, "echo": "e1" }""", "echo")]
    [InlineData("""{ "post_type": "notice", "notice_type": "group_increase" }""", "notice")]
    public void 生命周期心跳与回包事件_忽略不下发(string raw, string expectedHint)
    {
        var result = NapCatEventNormalizer.Normalize(raw);

        Assert.False(result.Handled, $"reason={result.IgnoreReason}");
        Assert.Contains(expectedHint, result.IgnoreReason);
    }

    [Fact]
    public void 白名单_经规范化事件正常过滤_幂等去重复用()
    {
        var raw = """
            { "post_type": "message", "message_type": "group", "message_id": 1001,
              "user_id": 1, "group_id": 20002, "message": "在吗" }
            """;
        var result = NapCatEventNormalizer.Normalize(raw);

        var pipeline = CreatePipeline(whitelist: ["20002"]);
        var r1 = pipeline.HandleDispatch(result.DispatchType, result.MappedJson);
        var r2 = pipeline.HandleDispatch(result.DispatchType, result.MappedJson);

        Assert.Equal(PipelineAction.Accepted, r1.Action);
        Assert.Equal(PipelineAction.Ignored, r2.Action); // 幂等
        Assert.Single(_received);

        // 未命中白名单的另一个群
        var rawOther = raw.Replace("20002", "30003").Replace("1001", "1002");
        var resultOther = NapCatEventNormalizer.Normalize(rawOther);
        var r3 = pipeline.HandleDispatch(resultOther.DispatchType, resultOther.MappedJson);
        Assert.Equal(PipelineAction.Ignored, r3.Action); // 白名单过滤
    }

    // ================= 反向 WS 服务端 =================

    /// <summary>启动一个反向监听（端口 0 = 系统分配）的 NapCat 客户端，返回 (client, 实际端口)。</summary>
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

    [Fact]
    public async Task 反向WS_token不匹配_以401拒绝()
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
            var bytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();

            var buffer = new byte[1024];
            var received = await stream.ReadAsync(buffer);
            var response = Encoding.ASCII.GetString(buffer, 0, received);

            Assert.StartsWith("HTTP/1.1 401", response);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task 反向WS_token匹配_完成握手并分发消息()
    {
        var (client, port) = await StartReverseAsync(token: "good-token");
        var dispatched = new List<(string Type, string Data)>();
        client.DispatchReceived += (_, e) => dispatched.Add(e);
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer good-token");
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

            var message = """
                { "post_type": "message", "message_type": "group", "message_id": 555,
                  "user_id": 10, "group_id": 20, "message": [ { "type": "text", "data": { "text": "反向WS消息" } } ] }
                """;
            var payload = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);

            for (var i = 0; i < 50 && dispatched.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            var (type, data) = Assert.Single(dispatched);
            Assert.Equal(GroupEventTypes.GroupMessageCreate, type);
            using var doc = JsonDocument.Parse(data);
            var ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
            Assert.Equal("555", ev.Id);
            Assert.Equal("20", ev.GroupOpenId);
            Assert.Equal("反向WS消息", ev.Content);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ================= 一键启动失败路径 =================

    private NapCatRunnerService CreateRunner(ConnectionSettings settings, Func<int?>? selfPort = null)
    {
        return new NapCatRunnerService(
            new IngestOptionsProvider
            {
                GetSettings = () => settings,
                DataDirectory = _dataDir
            },
            new XunitLogger(_output),
            selfPort);
    }

    private static async Task<NapCatRunnerStatus> WaitForTerminalAsync(NapCatRunnerService runner)
    {
        for (var i = 0; i < 50; i++)
        {
            var s = runner.Status;
            if (s.State is NapCatRunnerState.Failed or NapCatRunnerState.Running or NapCatRunnerState.Stopped)
            {
                return s;
            }

            await Task.Delay(100);
        }

        return runner.Status;
    }

    [Fact]
    public async Task 启动_未配置路径_明确报错()
    {
        var runner = CreateRunner(new ConnectionSettings { NapCatExePath = "" });
        await runner.StartNapCatAsync();
        var status = await WaitForTerminalAsync(runner);

        Assert.Equal(NapCatRunnerState.Failed, status.State);
        Assert.Contains("未配置", status.Detail);
    }

    [Fact]
    public async Task 启动_路径不存在_明确报错()
    {
        var runner = CreateRunner(new ConnectionSettings
        {
            NapCatExePath = Path.Combine(_dataDir, "不存在的NapCat.exe")
        });
        await runner.StartNapCatAsync();
        var status = await WaitForTerminalAsync(runner);

        Assert.Equal(NapCatRunnerState.Failed, status.State);
        Assert.Contains("找不到可执行文件", status.Detail);
    }

    [Fact]
    public async Task 启动_端口被占用_明确报错()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var occupiedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        try
        {
            var runner = CreateRunner(new ConnectionSettings
            {
                NapCatExePath = Environment.ProcessPath ?? "cmd.exe",
                NapCatReversePort = occupiedPort
            });
            await runner.StartNapCatAsync();
            var status = await WaitForTerminalAsync(runner);

            Assert.Equal(NapCatRunnerState.Failed, status.State);
            Assert.Contains($"端口 {occupiedPort}", status.Detail);
            Assert.Contains("占用", status.Detail);
        }
        finally
        {
            probe.Stop();
        }
    }

    [Fact]
    public async Task 启动_端口为本插件自身监听_不误报端口冲突()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var selfPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        try
        {
            // 路径不存在：若端口检查被（正确地）跳过，应报「找不到可执行文件」而非端口冲突
            var runner = CreateRunner(new ConnectionSettings
            {
                NapCatExePath = Path.Combine(_dataDir, "不存在的NapCat.exe"),
                NapCatReversePort = selfPort
            }, selfPort: () => selfPort);
            await runner.StartNapCatAsync();
            var status = await WaitForTerminalAsync(runner);

            Assert.Equal(NapCatRunnerState.Failed, status.State);
            Assert.Contains("找不到可执行文件", status.Detail);
        }
        finally
        {
            probe.Stop();
        }
    }

    // ================= 测试辅助 =================

    private readonly List<MessageRecord> _received = [];

    /// <summary>构造接入管道（可选白名单），MessageReceived 事件汇入 _received。</summary>
    private MessageIngestPipeline CreatePipeline(IReadOnlyList<string>? whitelist = null)
    {
        var pipeline = new MessageIngestPipeline(
            new MessageIdempotencyStore(_dataDir), new XunitLogger(_output));
        pipeline.MessageReceived += (_, m) => _received.Add(m);
        if (whitelist is not null)
        {
            pipeline.UpdateSettings(new ConnectionSettings { GroupWhitelist = whitelist });
        }

        return pipeline;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
