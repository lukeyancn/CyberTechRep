using ClassIng.Plugin.Services.MessageAccess;
using ClassIng.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace ClassIng.Tests;

/// <summary>模块 1 单元测试：接入管道（映射/幂等/白名单）。</summary>
public class IngestPipelineTests : IDisposable
{
    private readonly string _dataDir;
    private readonly MessageIngestPipeline _pipeline;
    private readonly List<MessageRecord> _received = [];
    private readonly ITestOutputHelper _output;

    public IngestPipelineTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        _pipeline = new MessageIngestPipeline(
            new MessageIdempotencyStore(_dataDir),
            new XunitLogger(output));
        _pipeline.MessageReceived += (_, m) => _received.Add(m);
    }

    private static string DispatchJson(string id, string groupOpenId = "grp-1", string content = "明天交数学作业",
        object? attachments = null)
    {
        return $$"""
            {
              "id": "{{id}}",
              "group_openid": "{{groupOpenId}}",
              "author": { "member_openid": "mem-9", "username": "课代表" },
              "content": "{{content}}",
              "timestamp": "2026-09-03T20:00:00+08:00",
              "attachments": {{(attachments is null ? "[]" : System.Text.Json.JsonSerializer.Serialize(attachments))}}
            }
            """;
    }

    [Fact]
    public void 同一消息重复推送_只产生一条记录_幂等()
    {
        var r1 = _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-1"));
        var r2 = _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-1"));

        Assert.Equal(PipelineAction.Accepted, r1.Action);
        Assert.Equal(PipelineAction.Ignored, r2.Action);   // 重复消息被幂等过滤
        Assert.Single(_received);
        Assert.Equal("msg-1", _received[0].MessageId);
    }

    [Fact]
    public void 群白名单_命中接收_未命中过滤()
    {
        _pipeline.UpdateSettings(new ConnectionSettings { GroupWhitelist = ["grp-ok"] });

        _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-a", "grp-ok"));
        _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-b", "grp-bad"));

        Assert.Single(_received);
        Assert.Equal("grp-ok", _received[0].GroupOpenId);
    }

    [Fact]
    public void 白名单为空_接收全部但仅告警一次()
    {
        _pipeline.UpdateSettings(new ConnectionSettings { GroupWhitelist = [] });

        _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-1", "grp-x"));
        _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-2", "grp-y"));

        Assert.Equal(2, _received.Count);
    }

    [Fact]
    public void 事件映射_文本与附件段_正确解析()
    {
        var attachments = new[]
        {
            new { content_type = "file", filename = "数学练习册.pdf", url = "https://x/y.pdf", size = 1024 },
            new { content_type = "image/png", filename = "p.png", url = "https://x/p.png", size = 2048 }
        };

        var result = _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate,
            DispatchJson("msg-m", attachments: attachments));

        Assert.Equal(PipelineAction.Accepted, result.Action);
        var record = _received.Single();
        Assert.Equal("mem-9", record.MemberOpenId);
        Assert.Equal(3, record.Segments.Count);

        var text = record.Segments[0];
        Assert.Equal(SegmentTypes.Text, text.Type);
        Assert.Equal("明天交数学作业", text.Text);

        var file = record.Segments[1];
        Assert.Equal(SegmentTypes.File, file.Type);
        Assert.Equal("数学练习册.pdf", file.FileName);
        Assert.Equal("https://x/y.pdf", file.Url);
        Assert.Equal(1024, file.FileSize);

        var image = record.Segments[2];
        Assert.Equal(SegmentTypes.Image, image.Type);
    }

    [Fact]
    public void 缺少消息id_事件被丢弃_不静默丢失()
    {
        var result = _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate,
            """{"group_openid":"grp-1","content":"无 id 消息"}""");

        Assert.Equal(PipelineAction.Dropped, result.Action);
        Assert.Empty(_received);
    }

    [Fact]
    public void 非群消息事件_被忽略()
    {
        var result = _pipeline.HandleDispatch("C2C_MESSAGE_CREATE", """{"id":"x"}""");
        Assert.Equal(PipelineAction.Ignored, result.Action);
    }

    [Fact]
    public void 幂等存储_跨实例持久化_重启后不重复()
    {
        _pipeline.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-p"));

        // 模拟重启：新建存储与管道实例
        var store2 = new MessageIdempotencyStore(_dataDir);
        var pipeline2 = new MessageIngestPipeline(store2, new XunitLogger(_output));
        var received2 = new List<MessageRecord>();
        pipeline2.MessageReceived += (_, m) => received2.Add(m);

        var r = pipeline2.HandleDispatch(GroupEventTypes.GroupMessageCreate, DispatchJson("msg-p"));

        Assert.Equal(PipelineAction.Ignored, r.Action);   // 重启不复活
        Assert.Empty(received2);
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

/// <summary>ITestOutputHelper → ILogger 适配（测试内可见结构化日志）。</summary>
public sealed class XunitLogger(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $"\n{exception}")}");
    }
}
