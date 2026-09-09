using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// NapCat 群文件下载链路测试：file 段规范化、get_group_file_url 直链解析器、
/// 反向 WS 全链路（收到文件消息 → 插件发起 API 调用 → 注入直链 → 分发）。
/// </summary>
public class NapCatFileUrlResolverTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dataDir;

    public NapCatFileUrlResolverTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
    }

    // ================= 规范化：file 段 =================

    [Fact]
    public void 群文件消息_规范化为file附件并进管道()
    {
        var raw = """
            { "post_type": "message", "message_type": "group", "message_id": 888,
              "user_id": 5, "group_id": 6,
              "message": [ { "type": "text", "data": { "text": "收作业" } },
                           { "type": "file", "data": { "file_id": "F77", "file": "语文笔记.docx", "url": "", "file_size": 4096 } } ] }
            """;

        var result = NapCatEventNormalizer.Normalize(raw);

        Assert.True(result.Handled, $"reason={result.IgnoreReason}");
        Assert.True(result.HasFileAttachment);

        using var doc = JsonDocument.Parse(result.MappedJson);
        var ev = GroupMessageEvent.Parse(doc.RootElement.Clone());
        var attachment = Assert.Single(ev.Attachments);
        Assert.Equal("file", attachment.ContentType);
        Assert.Equal("语文笔记.docx", attachment.FileName);
        Assert.Equal(4096, attachment.Size);

        // 管道零改动复用：产出 File 段记录（下游文件管线按 Url 下载归档）
        var pipeline = CreatePipeline();
        pipeline.HandleDispatch(result.DispatchType, result.MappedJson);
        var record = Assert.Single(_received);
        var segment = Assert.Single(record.Segments.Where(s => s.Type == SegmentTypes.File));
        Assert.Equal("语文笔记.docx", segment.FileName);
    }

    [Fact]
    public void 图片消息_不标记file直链解析()
    {
        var raw = """
            { "post_type": "message", "message_type": "group", "message_id": 889, "user_id": 5, "group_id": 6,
              "message": [ { "type": "image", "data": { "file": "a.png", "url": "https://example.com/a.png" } } ] }
            """;

        var result = NapCatEventNormalizer.Normalize(raw);

        Assert.True(result.Handled);
        Assert.False(result.HasFileAttachment);
    }

    // ================= 直链解析器 =================

    [Fact]
    public async Task 直链解析_API成功_注入url()
    {
        var mapped = """
            {"id":"1","group_openid":"20","content":"","attachments":[{"content_type":"file","filename":"a.pdf","url":"","file_id":"F1"}]}
            """;
        string? requestedAction = null;
        IReadOnlyDictionary<string, object?>? requestedParams = null;

        var json = await NapCatFileUrlResolver.EnrichAsync(mapped, (action, parameters, _) =>
        {
            requestedAction = action;
            requestedParams = parameters;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { url = "https://x/a.pdf" }));
        }, null, CancellationToken.None);

        Assert.Equal("get_group_file_url", requestedAction);
        Assert.Equal("F1", requestedParams?["file_id"]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("https://x/a.pdf", doc.RootElement.GetProperty("attachments")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task 直链解析_私聊消息_跳过API()
    {
        var mapped = """
            {"id":"1","group_openid":"private:42","attachments":[{"content_type":"file","filename":"a.pdf","url":"","file_id":"F1"}]}
            """;

        var json = await NapCatFileUrlResolver.EnrichAsync(mapped,
            (_, _, _) => throw new InvalidOperationException("私聊不应调用群文件 API"),
            null, CancellationToken.None);

        Assert.Equal(mapped, json);
    }

    [Fact]
    public async Task 直链解析_API失败_保留事件自带url()
    {
        var mapped = """
            {"id":"1","group_openid":"20","attachments":[{"content_type":"file","filename":"a.pdf","url":"https://orig/a.pdf","file_id":"F1"}]}
            """;

        var json = await NapCatFileUrlResolver.EnrichAsync(mapped,
            (_, _, _) => Task.FromResult<JsonElement?>(null), null, CancellationToken.None);

        Assert.Equal(mapped, json); // 无变更 → 原 JSON 原样返回
    }

    [Fact]
    public async Task 直链解析_无file段_原样返回()
    {
        var mapped = """
            {"id":"1","group_openid":"20","attachments":[{"content_type":"image/png","filename":"a.png","url":"https://x/a.png"}]}
            """;

        var json = await NapCatFileUrlResolver.EnrichAsync(mapped,
            (_, _, _) => throw new InvalidOperationException("无 file 段不应调用 API"),
            null, CancellationToken.None);

        Assert.Equal(mapped, json);
    }

    // ================= 反向 WS 全链路 =================

    [Fact]
    public async Task 反向WS_群文件消息_自动解析直链后分发()
    {
        var client = new NapCatWsClient(new NapCatWsClientOptions
        {
            WsUrl = "",
            ReverseListenPort = 0
        }, new XunitLogger(_output));
        var dispatched = new List<(string Type, string Data)>();
        client.DispatchReceived += (_, e) => dispatched.Add(e);
        await client.StartAsync();
        for (var i = 0; i < 50 && client.ListeningPort is null; i++)
        {
            await Task.Delay(100);
        }

        Assert.NotNull(client.ListeningPort);
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{client.ListeningPort}/"), CancellationToken.None);

            await SendTextAsync(ws, """
                { "post_type": "message", "message_type": "group", "message_id": 666,
                  "user_id": 10, "group_id": 20,
                  "message": [ { "type": "file", "data": { "file_id": "F123", "file": "数学作业.pdf", "file_size": "2048" } } ] }
                """);

            // 期望插件经同一连接发起 get_group_file_url 调用
            var echo = "";
            for (var i = 0; i < 100 && echo.Length == 0; i++)
            {
                var text = await ReceiveTextAsync(ws, CancellationToken.None);
                if (text.Length == 0)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("action", out var action)
                    && action.GetString() == "get_group_file_url")
                {
                    echo = doc.RootElement.GetProperty("echo").GetString() ?? "";
                    var parameters = doc.RootElement.GetProperty("params");
                    Assert.Equal("F123", parameters.GetProperty("file_id").GetString());
                    Assert.Equal("20", parameters.GetProperty("group_id").GetString());
                }
            }

            Assert.NotEqual("", echo);

            // 模拟 NapCat 回包（带新鲜直链）
            await SendTextAsync(ws, $$"""
                { "status": "ok", "retcode": 0, "data": { "url": "https://dl.example.com/math-hw.pdf" }, "echo": "{{echo}}" }
                """);

            for (var i = 0; i < 100 && dispatched.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            var (dispatchType, dispatchData) = Assert.Single(dispatched);
            Assert.Equal(GroupEventTypes.GroupMessageCreate, dispatchType);
            using var rdoc = JsonDocument.Parse(dispatchData);
            var ev = GroupMessageEvent.Parse(rdoc.RootElement.Clone());
            var attachment = Assert.Single(ev.Attachments);
            Assert.Equal("https://dl.example.com/math-hw.pdf", attachment.Url);
            Assert.Equal("数学作业.pdf", attachment.FileName);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ================= 测试辅助 =================

    private readonly List<MessageRecord> _received = [];

    private MessageIngestPipeline CreatePipeline()
    {
        var pipeline = new MessageIngestPipeline(
            new MessageIdempotencyStore(_dataDir), new XunitLogger(_output));
        pipeline.MessageReceived += (_, m) => _received.Add(m);
        return pipeline;
    }

    private static async Task SendTextAsync(WebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return "";
            }

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
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
