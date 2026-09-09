using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using CyberTechRep.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 4 断点续传集成测试（游标为主 + 启动核对兜底）。
/// <para>
/// 覆盖三条「不漏、不重」的关键性质：
/// <list type="number">
/// <item><b>稳定键同源</b>：同一条消息在实时路径（<c>RecordResumeCursor</c>：原始时间 + 事件 JSON）
/// 与核对路径（<c>ExtractMessages</c>）算出的稳定键必须逐字节相等，否则同秒兜底去重失效；</item>
/// <item><b>时间线不污染</b>：协议端未提供 <c>time</c> 的消息不得用本地接收时刻推进游标时间线
/// （旧缺陷：本地时刻会把真实时间更早的历史消息误判为「已覆盖」而漏补）；</item>
/// <item><b>核对只补缺口</b>：重启后核对同一批历史消息，已入档的不重复注入，断线期间的缺口被补齐。</item>
/// </list>
/// </para>
/// </summary>
public class NapCatResumeIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dataDir;
    private readonly ConcurrentQueue<MessageRecord> _received = new();

    public NapCatResumeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", "resume-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>实时消息事件（含原始 time=1725710000）。</summary>
    private const string LiveMessageJson = """
        { "post_type": "message", "message_type": "group", "message_id": 12345,
          "user_id": 666, "group_id": 888888, "time": 1725710000,
          "sender": { "nickname": "数学课代表" },
          "message": [ { "type": "text", "data": { "text": "明天交数学作业P23" } } ] }
        """;

    /// <summary>断线期间产生的新消息（核对时补齐）。</summary>
    private const string GapMessageJson = """
        { "post_type": "message", "message_type": "group", "message_id": 12346,
          "user_id": 666, "group_id": 888888, "time": 1725710001,
          "sender": { "nickname": "数学课代表" },
          "message": [ { "type": "text", "data": { "text": "数学练习册第8页" } } ] }
        """;

    // ================= ① 稳定键同源 =================

    [Fact]
    public void 实时与核对_同一消息生成相同稳定键()
    {
        // 实时路径：与 MessageIngestService.RecordResumeCursor 完全同口径
        var normalized = NapCatEventNormalizer.Normalize(LiveMessageJson);
        Assert.True(normalized.Handled, normalized.IgnoreReason);
        using var doc = JsonDocument.Parse(normalized.MappedJson);
        var ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
        var record = MessageIngestPipeline.MapToRecord(ev, normalized.MappedJson);
        var liveKey = NapCatResumeCursorStore.ContentHashKey(record.SourceTimestampUnix, record.RawJsonSnapshot);

        // 核对路径：get_group_msg_history 回包 → ExtractMessages
        var history = NapCatBackfillService.ExtractMessages(HistoryData(LiveMessageJson));
        var item = Assert.Single(history);

        Assert.Equal(record.MessageId, item.MessageId);
        Assert.Equal(record.SourceTimestampUnix, item.TimestampUnix);
        Assert.Equal(1725710000, item.TimestampUnix);
        Assert.Equal(liveKey, item.ContentHash);
    }

    [Fact]
    public void 实时入档的游标_能覆盖核对路径算出的稳定键()
    {
        using var cursor = new NapCatResumeCursorStore(_dataDir);

        // 实时路径记录（与 RecordResumeCursor 同口径）
        var normalized = NapCatEventNormalizer.Normalize(LiveMessageJson);
        using (var doc = JsonDocument.Parse(normalized.MappedJson))
        {
            var record = MessageIngestPipeline.MapToRecord(
                GroupMessageEvent.Parse(doc.RootElement.Clone()), normalized.MappedJson);
            cursor.Record(record.GroupOpenId, record.MessageId, record.SourceTimestampUnix,
                NapCatResumeCursorStore.ContentHashKey(record.SourceTimestampUnix, record.RawJsonSnapshot));
        }

        // 核对路径判定：同一条消息必须判为「已覆盖」（同秒兜底键命中）
        var item = Assert.Single(NapCatBackfillService.ExtractMessages(HistoryData(LiveMessageJson)));
        Assert.True(cursor.IsCovered("888888", item.MessageId, item.TimestampUnix, item.ContentHash),
            "实时路径与核对路径的稳定键不一致：重启核对会重复注入同一秒的消息");
    }

    // ================= ② 无原始时间不推进时间线 =================

    [Fact]
    public async Task 实时消息无原始时间_不推进时间线_只按消息id去重()
    {
        var (service, cursor, _, conn) = await StartReverseAsync();
        await using (conn)
        {
            const string noTime = """
                { "post_type": "message", "message_type": "group", "message_id": 90001,
                  "user_id": 666, "group_id": 888888, "sender": { "nickname": "老师" },
                  "message": "无时间戳的作业消息" }
                """;
            await conn.SendTextAsync(noTime);

            await TestWait.WaitForAsync(() => _received.Any(m => m.MessageId == "90001"),
                TimeSpan.FromSeconds(5), "实时消息入档");
            await TestWait.WaitForAsync(() => cursor.Get("888888") is not null,
                TimeSpan.FromSeconds(5), "游标已记录");

            var entry = cursor.Get("888888")!;
            Assert.Equal("90001", entry.LastMessageId);
            // 关键：不得写入本地接收时刻（否则时间线被污染，真实时间更早的历史消息会被误判为已覆盖）
            Assert.Equal(0, entry.LastTimestampUnix);
        }

        await service.StopAsync();
    }

    // ================= ③ 核对只补缺口 =================

    [Fact]
    public async Task 启动核对_补齐缺口且不重复注入已入档消息()
    {
        var (service, cursor, provider, conn) = await StartReverseAsync();
        await using (conn)
        {
            // 实时消息先入档并推进游标（模拟重启前已处理）
            await conn.SendTextAsync(LiveMessageJson);
            await TestWait.WaitForAsync(() => _received.Any(m => m.MessageId == "12345"),
                TimeSpan.FromSeconds(5), "实时消息入档");

            // 核对回包：同一条已入档消息 + 一条断线期间的新消息
            conn.OnApiCall = root =>
            {
                var echo = root.GetProperty("echo").GetString();
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    retcode = 0,
                    echo,
                    data = new
                    {
                        messages = new[]
                        {
                            JsonDocument.Parse(LiveMessageJson).RootElement.Clone(),
                            JsonDocument.Parse(GapMessageJson).RootElement.Clone()
                        }
                    }
                });
            };

            using var backfill = new NapCatBackfillService(
                service, cursor, provider, new XunitLogger(_output));
            await backfill.RunBackfillSafeAsync(CancellationToken.None);

            // 已入档消息被游标覆盖（不重复注入），缺口消息补齐
            Assert.Equal(1, backfill.LastReport.Injected);
            Assert.Equal(1, backfill.LastReport.Skipped);
            Assert.Single(_received, m => m.MessageId == "12345");
            Assert.Single(_received, m => m.MessageId == "12346");

            // 补齐消息按原始时间入档（不是本地当前时间）
            var gap = _received.Single(m => m.MessageId == "12346");
            Assert.Equal(1725710001, gap.SourceTimestampUnix);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1725710001).ToLocalTime(), gap.ReceivedAt);
        }

        await service.StopAsync();
    }

    // ================= 历史条目解析的边界 =================

    [Fact]
    public void ExtractMessages_缺消息id与非法条目被跳过()
    {
        var data = JsonDocument.Parse("""
            { "messages": [
                { "message_id": 1, "group_id": 2, "user_id": 3, "time": 100, "message": "作业" },
                { "group_id": 2, "user_id": 3, "time": 101, "message": "缺 id" },
                42,
                { "message_id": "5", "group_id": 2, "user_id": 3, "time": 102, "message": "字符串 id" }
            ] }
            """).RootElement.Clone();

        var items = NapCatBackfillService.ExtractMessages(data);

        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { "1", "5" }, items.Select(i => i.MessageId).ToArray());
        Assert.Equal(new[] { 100L, 102L }, items.Select(i => i.TimestampUnix).ToArray());
    }

    [Fact]
    public void ExtractMessages_缺少时间字段_时间戳为0_但消息仍可补齐()
    {
        var data = JsonDocument.Parse("""
            { "messages": [ { "message_id": 77, "group_id": 2, "user_id": 3, "message": "无时间" } ] }
            """).RootElement.Clone();

        var item = Assert.Single(NapCatBackfillService.ExtractMessages(data));
        Assert.Equal(0, item.TimestampUnix);
        // 时间缺失时稳定键退化为纯内容哈希（无「时间戳:」前缀），游标不推进时间线
        Assert.Equal(NapCatResumeCursorStore.ContentHashKey(0, item.MappedJson), item.ContentHash);
        Assert.DoesNotContain(":", item.ContentHash);
    }

    // ================= 测试辅助 =================

    /// <summary>启动反向 WS 模式（端口 0 = 系统分配）的接入服务，并接入一个假 NapCat 连接。</summary>
    private async Task<(MessageIngestService Service, NapCatResumeCursorStore Cursor,
        IngestOptionsProvider Provider, FakeNapCatConnection Conn)> StartReverseAsync()
    {
        var port = GetFreePort();
        var settings = new ConnectionSettings
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatWsUrl = "",
            NapCatReversePort = port,
            GroupWhitelist = ["888888"],
            NapCatBackfillOnConnect = true,
            NapCatBackfillCount = 50,
            ReconnectInitialDelaySec = 1,
            ReconnectBackoffFactor = 2.0,
            ReconnectMaxDelaySec = 2
        };
        var provider = new IngestOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = _dataDir
        };
        var cursor = new NapCatResumeCursorStore(_dataDir, new XunitLogger(_output));
        var service = new MessageIngestService(
            provider, new XunitLogger(_output), configureClientOptions: null, resumeCursor: cursor);
        service.MessageReceived += (_, m) => _received.Enqueue(m);

        await service.StartAsync();
        await TestWait.WaitForAsync(() => service.ActiveReverseListenerPort == port,
            TimeSpan.FromSeconds(5), "反向 WS 监听就绪");

        var conn = await FakeNapCatConnection.ConnectAsync(port);
        return (service, cursor, provider, conn);
    }

    private static JsonElement HistoryData(params string[] messages)
        => JsonDocument.Parse("{\"messages\":[" + string.Join(",", messages) + "]}").RootElement.Clone();

    /// <summary>取一个空闲端口（反向 WS 监听需要具体端口，settings 的 0 表示禁用）。</summary>
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    /// <summary>假 NapCat 连接：接收插件侧帧（API 调用），按 <see cref="OnApiCall"/> 回包。</summary>
    private sealed class FakeNapCatConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeNapCatConnection(ClientWebSocket ws)
        {
            _ws = ws;
            _loop = Task.Run(LoopAsync);
        }

        /// <summary>API 调用处理：入参为插件发出的 {action,params,echo} 帧，返回回包 JSON（null = 不回包）。</summary>
        public Func<JsonElement, string?>? OnApiCall { get; set; }

        public static async Task<FakeNapCatConnection> ConnectAsync(int port)
        {
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
            return new FakeNapCatConnection(ws);
        }

        public Task SendTextAsync(string json)
            => _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        private async Task LoopAsync()
        {
            var buffer = new byte[64 * 1024];
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                try
                {
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);
                }
                catch (Exception)
                {
                    return; // 关闭/取消：接收循环退出
                }

                var text = sb.ToString();
                if (text.Length == 0)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("echo", out var echo)
                        && echo.ValueKind == JsonValueKind.String
                        && doc.RootElement.TryGetProperty("action", out _))
                    {
                        var reply = OnApiCall?.Invoke(doc.RootElement);
                        if (reply is not null)
                        {
                            await SendTextAsync(reply);
                        }
                    }
                }
                catch (JsonException)
                {
                    // 非 JSON 帧：忽略
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // 已断开
            }

            _ws.Dispose();
            try
            {
                await _loop;
            }
            catch
            {
                // 接收循环异常退出：忽略
            }

            _cts.Dispose();
        }
    }
}
